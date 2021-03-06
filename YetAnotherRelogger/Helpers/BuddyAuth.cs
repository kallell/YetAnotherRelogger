﻿using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using YetAnotherRelogger.Helpers.Bot;
using YetAnotherRelogger.Helpers.Tools;

namespace YetAnotherRelogger.Helpers
{
    public class BuddyAuth
    {
        #region singleton

        private static readonly BuddyAuth instance = new BuddyAuth();

        static BuddyAuth()
        {
        }

        private BuddyAuth()
        {
        }

        public static BuddyAuth Instance
        {
            get { return instance; }
        }

        #endregion

        private readonly HashSet<BuddyAuthWebClient> _buddyAuthWebClients = new HashSet<BuddyAuthWebClient>();

        public bool KillSession(BotClass bot)
        {
            string username = bot.Demonbuddy.BuddyAuthUsername;
            string password = bot.Demonbuddy.BuddyAuthPassword;

            // if username and password are empty stop here!
            if (username.Length <= 0 && password.Length <= 0)
                return false;

            BuddyAuthWebClient client =
                _buddyAuthWebClients.FirstOrDefault(c => c.Username.Equals(username) && c.Password.Equals(password));
            if (client == null)
            {
                client = new BuddyAuthWebClient(username, password);
                _buddyAuthWebClients.Add(client);
            }
            client.KillSession(bot);
            return false;
        }
    }

    public class BuddyAuthWebClient
    {
        private const string LogOnUrl = "http://buddyauth.com/Account/LogOn";
        private const string SessionsUrl = "http://buddyauth.com/User/Sessions";
        private readonly CookieAwareWebClient _webClient;
        private readonly object _webClientLock = new object();
        public string Password;
        public string Username;
        private Cookie _authCookie;

        public BuddyAuthWebClient(string username, string password)
        {
            Username = username;
            Password = password;
            _webClient = new CookieAwareWebClient();
            _webClient.Headers["Content-type"] = "application/x-www-form-urlencoded";
            _webClient.Headers["Host"] = "buddyauth.com";
            _webClient.Headers["User-Agent"] =
                "Mozilla/5.0 (Windows NT 6.1; WOW64; rv:13.0) Gecko/20100101 Firefox/13.0.1";
        }

        public bool IsLoggedIn
        {
            get
            {
                if (_authCookie == null)
                    return false;
                return _authCookie.Expires >= DateTime.Now;
            }
        }

        public List<Session> GetSessions()
        {
            var sessions = new List<Session>();

            if (!IsLoggedIn)
                Login();
            lock (_webClientLock)
            {
                string input = _webClient.DownloadString("http://buddyauth.com/User/Sessions");
                var regex =
                    new Regex(
                        "<tr>[^<]+<td>.+name=\"selectedSessions\\[([0-9]+)\\].Id\".+value=\"(.+)\"[^<]+<input[^<]+<input[^<]+<[^<]+<td>Demonbuddy</td>[^<]+<td>([^<]+)<");
                var session = new Session();
                foreach (Match match in regex.Matches(input))
                {
                    // Get login time for each session
                    // Sample: 7/30/2012 7:51:45 PM
                    string number = match.Groups[1].ToString();
                    string id = match.Groups[2].ToString();
                    string time = match.Groups[3].ToString().Trim();
                    DateTime result = DateTime.Parse(time, new CultureInfo("en-US", false));

                    Debug.WriteLine("Found Session: id:{0} time:{1}", id, result);
                    session.id = int.Parse(id);
                    session.time = result;
                    session.number = int.Parse(number);
                    sessions.Add(session);
                }
            }

            return sessions;
        }

        public void KillSession(BotClass bot)
        {
            lock (_webClientLock)
            {
                DateTime logintime = bot.Demonbuddy.LoginTime;
                _webClient.Headers["Referer"] = "http://buddyauth.com/User/Sessions";
                var data = new NameValueCollection();
                Session match = GetSessions()
                    .Where(
                        i =>
                            logintime.Subtract(i.time).TotalSeconds < 16 &&
                            logintime.Subtract(i.time).TotalSeconds > -16)
                    .OrderBy(i => logintime.Subtract(i.time).TotalSeconds).FirstOrDefault();

                if (match.id == 0)
                {
                    Logger.Instance.Write(bot, "BuddyAuth: No session found.");
                    return;
                }

                data.Set("selectedSessions[" + match.number + "].Id", Convert.ToString(match.id));
                data.Set("selectedSessions[" + match.number + "].IsChecked", "true");

                try
                {
                    int retry = 3;
                    do
                    {
                        try
                        {
                            _webClient.UploadValues("http://buddyauth.com/User/Sessions", data);
                            Logger.Instance.Write(bot, "BuddyAuth: Session with id: {0} killed! (Time difference: {1})",
                                match.id, logintime.Subtract(match.time).TotalSeconds);
                            return;
                        }
                        catch (WebException wex)
                        {
                            HttpStatusCode code = ((HttpWebResponse) wex.Response).StatusCode;
                            if (code != HttpStatusCode.InternalServerError &&
                                code != HttpStatusCode.BadGateway &&
                                code != HttpStatusCode.ServiceUnavailable &&
                                code != HttpStatusCode.GatewayTimeout &&
                                code != HttpStatusCode.RequestTimeout)
                            {
                                Logger.Instance.Write(bot, "Failed: {0}", wex.Message);
                                return;
                            }

                            Logger.Instance.Write(bot, "Failed: {0} (next retry in 5 seconds) [{1}]", wex.Message,
                                3 - retry + 1);
                            Thread.Sleep(5000);
                        }
                    } while (retry-- > 0);
                }
                catch (Exception ex)
                {
                    DebugHelper.Write(bot, "BuddyAuth session killer failed!");
                    DebugHelper.Exception(ex);
                    return;
                }
                Logger.Instance.Write(bot, "BuddyAuth: No session found.");
            }
        }

        public bool Login()
        {
            lock (_webClientLock)
            {
                _webClient.Headers["Referer"] = "http://buddyauth.com/Account/LogOn";
                _webClient.UploadString("http://buddyauth.com/Account/LogOn",
                    "UserName=" + Username + "&Password=" + Password + "&RememberMe=true");
                foreach (Cookie cookie in _webClient.Cookies.GetCookies(new Uri("http://buddyauth.com/Account/LogOn")))
                {
                    if (!cookie.ToString().Contains(".ASPXAUTH="))
                        continue;
                    _authCookie = cookie;
                    return true;
                }
                return false;
            }
        }

        public struct Session
        {
            public int id;
            public int number;
            public DateTime time;
        }
    }
}