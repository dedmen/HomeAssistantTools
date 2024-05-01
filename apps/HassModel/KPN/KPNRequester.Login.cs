using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Encodings.Web;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace KPNAPIPoll
{
    internal partial class KPNRequester
    {
        private const string client_id = "dtLWqtxw84z2hkPAb3vzJb1gmVbg1xnJoB0vnFTX";
        const string cigateway_login = "https://api.kpn.com/cigateway/v2/login?mijnkpnapp_version=6.13.0&mijnkpnapp_buildnr=9273";
        const string cigateway_redirectToApp = "https://api.kpn.com/cigateway/v1/redirectToApplication?mijnkpnapp_version=6.13.0&mijnkpnapp_buildnr=9273";
        const string appToken = "https://mijn.kpn.com/api/auth/v1/token";

        private const string appUserAgent = "MijnKPN/6.13.0(nl.kpn.mijn; build:9273; Android 9)";
        private const string cigatewayUserAgent = "Mozilla/5.0 (Linux; Android 9; Android SDK built for x86_64 Build/PSR1.180720.122; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/69.0.3497.100 Mobile Safari/537.36";

        struct appTokenCache
        {
            public DateTime expiry { get; set; }
            public string accessToken { get; set; }
            public string refreshToken { get; set; }
            public string csrfSessionKey { get; set; }
            public Cookie mijnSessionId { get; set; }
        }

        private string authHeader = "";
        private string csrfSessionKey = "";

        public void DoLogin()
        {
            if (File.Exists(AppTokenCacheFile))
            {
                // we have token we can potentially refresh

                var cacheData = JsonSerializer.Deserialize<appTokenCache>(File.ReadAllText(AppTokenCacheFile));

                if (cacheData.expiry > DateTime.Now.AddMinutes(2)) // still valid for atleast 2 minuets
                {
                    Log("Load AppToken from cache");

                    authHeader = $"Bearer {cacheData.accessToken}";
                    csrfSessionKey = cacheData.csrfSessionKey;
                    cookies.Add(cacheData.mijnSessionId);
                    return;
                }

                Log("AppToken cache expired, try refresh");

                Retry:
                try
                {
                    //Dns.GetHostAddresses("mijn.kpn.com"); // Maybe that helps with name resolution failure spam, force it to try to resolve. instead of accepting that cache says it doesn't work

                    if (GetRefreshAppToken(cacheData.refreshToken))
                        return;
                }
                catch (WebException ex)
                {
                    Log($"Ex {ex.Status}");
                    if (ex.Status == WebExceptionStatus.NameResolutionFailure)
                        goto Retry;
                }
                

                // Refresh failed, need to do full flow
            }

            GetLoginToken(out var appTokenCode);
            GetNewAppToken(ref appTokenCode);
        }

        struct DynResLoginToken
        {
            public string status { get; set; }
            public string redirect_url { get; set; }
        }

        public void GetLoginToken(out string appTokenCode, bool force = false)
        {
            // cigateway has its own cookie store, the session id we have is only temporary

            var cookies = new CookieContainer(); // Create cookies!

            // Base cookies that are always present
            cookies.Add(new Cookie("mijnkpnapp", "1", "/", domain: "kpn.com"));
            cookies.Add(new Cookie("mijnkpnapp_locale", "en", "/", domain: "kpn.com"));
            cookies.Add(new Cookie("mijnkpnapp_colorscheme", "light", "/", domain: "kpn.com"));


            /*
            if (File.Exists(LoginCacheFile) && !force)
            {
                JObject cacheData = JObject.Parse(File.ReadAllText(LoginCacheFile));

                var expiryTime = cacheData["expiry"].ToObject<DateTime>();

                if (expiryTime > DateTime.Now) // still valid
                {
                    // load cookies
                    Log("Load login from cache");

                    csrfSessionKey = cacheData["csrfSession"].ToObject<string>();
                    var sessionCookie = cacheData["cookie"].ToObject<Cookie>();
                    cookies.Add(sessionCookie);
                    return;
                }
                else
                    Log("Login cache expired");
            }
            */
            // Login and get session id into our cookies

            string csrfSessionKey = "";

            {
                var request = WebRequest.Create(cigateway_login) as HttpWebRequest;
                request.CookieContainer = cookies; // Assign it some cookies 

                request.ContentType = "application/json";
                request.Method = "POST";
                request.Referer = "https://inloggen.kpn.com/";
                request.Headers.Add("x-requested-with", "nl.kpn.mijn");
                request.UserAgent = cigatewayUserAgent;

                string username = _cfg["KPN:Username"];
                string password = _cfg["KPN:Password"];

                string bodyContent = $"{{ " +
                                     $"\"username\": \"{username}\", " +
                                     $"\"password\": \"{System.Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(password))}\"" +
                                     $"}}";

                byte[] bytes = Encoding.ASCII.GetBytes(bodyContent);
                request.ContentLength = bytes.Length;
                using (Stream loginStream = request.GetRequestStream())
                {
                    loginStream.Write(bytes, 0, bytes.Length);
                }

                var response = request.GetResponse();
                csrfSessionKey = response.Headers.Get("csrf-session-key");

                // Response are set cookies for KSR and sessionid
                // expected body
                /*
                {
                    "status": "ok",
                    "status_reason": null
                }
                */


                // KSR is a JWT token, usually AES256 encrypted
            }

            // We can now translate that sessionid into a real token
            {
                var request = WebRequest.Create(cigateway_redirectToApp) as HttpWebRequest;
                request.CookieContainer = cookies; // Assign it some cookies 

                request.ContentType = "application/x-www-form-urlencoded";
                request.Method = "POST";
                request.Referer = "https://inloggen.kpn.com/";
                request.Headers.Add("x-requested-with", "nl.kpn.mijn");
                request.Headers.Add("csrf-session-key", csrfSessionKey);
                request.UserAgent = cigatewayUserAgent;

                byte[] bytes = Encoding.ASCII.GetBytes($"client_id={client_id}&redirect_uri=https%3A%2F%2Fmijn.kpn.com%2Foauth_redirect");
                request.ContentLength = bytes.Length;
                using (Stream loginStream = request.GetRequestStream())
                {
                    loginStream.Write(bytes, 0, bytes.Length);
                }

                // Response are new set cookies for KSR and sessionid
                // expected body
                /*
                {
                   "redirect_url": "https://mijn.kpn.com/oauth_redirect?code=oKxxxxmG",
                   "status": "ok"
                   }
                */

                try
                {
                    using (WebResponse getResponse = request.GetResponse())
                    {
                        using (StreamReader sr = new StreamReader(getResponse.GetResponseStream()))
                        {
                            var result = sr.ReadToEnd();//Read logged in webpage
                            Console.WriteLine(result);

                            var res = JsonSerializer.Deserialize<DynResLoginToken>(result);

                            string redirUri = res.redirect_url;
                            appTokenCode = redirUri.Substring(redirUri.IndexOf("code=") + 5);
                            // {
                            //     "status": "ok",
                            //     "redirect_url": "https://mijn.kpn.com/#/overzicht?&code=7MNvxxgdr"
                            // }

                            var cooks = cookies.GetCookies(new Uri("https://kpn.com/"));

                            if (cooks.Count < 1)
                            {
                                Log("Login failed, cookie count invalid");
                                return;
                            }

                            return;
                        }
                    }
                }
                catch (System.Net.WebException exception)
                {
                    Log($"GetLoginToken threw: ${exception.ToString()}");
                    //// login again and retry
                    //if (exception.Status == WebExceptionStatus.ProtocolError &&
                    //    ((System.Net.HttpWebResponse)exception.Response).StatusCode == HttpStatusCode.Unauthorized)
                    //{
                    //
                    //    GetLoginToken(cookies, out csrfSessionKey, true);
                    //    GetAppInit(cookies, ref csrfSessionKey, out appTokenCode, true);
                    //}
                    //else
                        throw;
                }
            }
        }

        struct DynResNewAppToken
        {
            public string access_token { get; set; }
            public string token_type { get; set; }
            public int expires_in { get; set; }
            public string refresh_token { get; set; }
            public string scope { get; set; }
            public string id_token { get; set; }
        }

        public void GetNewAppToken(ref string appTokenCode)
        {
            //if (File.Exists(AppTokenCacheFile) && !force)
            //{
            //    JObject cacheData = JObject.Parse(File.ReadAllText(AppTokenCacheFile));
            //
            //    var expiryTime = cacheData["expiry"].ToObject<DateTime>();
            //
            //    if (expiryTime > DateTime.Now) // still valid
            //    {
            //        Log("Load AppToken from cache");
            //
            //        oauthCookie = cacheData["cookie"].ToObject<Cookie>();
            //        cookies.Add(oauthCookie);
            //        return;
            //    }
            //    else
            //        Log("AppToken cache expired");
            //}


            // Cookie precondition
            // ci_device_uuid=12341234-1234-1234-1234-123412341234; mijnkpnapp=1; mijnkpnapp_locale=en; mijnkpnapp_colorscheme=light;


            var request = WebRequest.Create(appToken) as HttpWebRequest; //we know we get redirected too here, so just go there. 
            request.UserAgent = appUserAgent;
            request.CookieContainer = cookies; // Assign it some cookies 
            request.ContentType = "application/x-www-form-urlencoded";
            request.Method = "POST";

            // client_id={client_id}&code={appTokenCode}&code_verifier=&grant_type=authorization_code&redirect_uri=https://mijn.kpn.com/oauth_redirect&response_type=id_token+token
            var requestString = $"client_id={client_id}&code={appTokenCode}&code_verifier=&grant_type=authorization_code&redirect_uri=https://mijn.kpn.com/oauth_redirect&response_type=id_token+token";
            var bytes = Encoding.ASCII.GetBytes(requestString);
            request.ContentLength = bytes.Length;
            using (Stream loginStream = request.GetRequestStream())
            {
                loginStream.Write(bytes, 0, bytes.Length);
            }


            try
            {
                using (WebResponse getResponse = request.GetResponse())
                {
                    using (StreamReader sr = new StreamReader(getResponse.GetResponseStream()))
                    {
                        var result = sr.ReadToEnd();//Read logged in webpage
                        Console.WriteLine(result);

                        var res = JsonSerializer.Deserialize<DynResNewAppToken>(result);

                        //{
                        //    "access_token": "qPXJSJ",
                        //    "token_type": "Bearer",
                        //    "expires_in": 3600,
                        //    "refresh_token": "7HXZ2IW",
                        //    "scope": ""
                        //}
                        
                        authHeader = $"Bearer {(string) res.access_token}";
                        csrfSessionKey = getResponse.Headers.Get("csrf-session-key");

                        var expiresSeconds = (int)res.expires_in;

                        File.WriteAllText(AppTokenCacheFile, JsonSerializer.Serialize(new appTokenCache
                        {
                            expiry = DateTime.Now.AddSeconds(expiresSeconds),
                            accessToken = (string)res.access_token,
                            refreshToken = (string)res.refresh_token,
                            csrfSessionKey = csrfSessionKey,
                            mijnSessionId = cookies.GetCookies(new Uri("https://mijn.kpn.com/"))["sessionid"]
                        }));
                    }
                }
            }
            catch (System.Net.WebException exception)
            {
                Log($"GetNewAppToken threw: ${exception.ToString()}");
                // login again and retry
                //if (exception.Status == WebExceptionStatus.ProtocolError &&
                //    ((System.Net.HttpWebResponse)exception.Response).StatusCode == HttpStatusCode.Unauthorized)
                //{
                //
                //    GetLoginToken(cookies, out csrfSessionKey, true);
                //    GetAppInit(cookies, ref csrfSessionKey, out appTokenCode, true);
                //    GetAppToken(cookies, ref csrfSessionKey, ref appTokenCode, out oauthCookie, true);
                //}
                //else
                    throw;
            }
        }


        struct DynResRefreshAppToken
        {
            public string access_token { get; set; }
            public string token_type { get; set; }
            public int expires_in { get; set; }
            public string refresh_token { get; set; }
            public string scope { get; set; }
            public string id_token { get; set; }
        }

        public bool GetRefreshAppToken(string refreshToken)
        {
            //if (File.Exists(AppTokenCacheFile) && !force)
            //{
            //    JObject cacheData = JObject.Parse(File.ReadAllText(AppTokenCacheFile));
            //
            //    var expiryTime = cacheData["expiry"].ToObject<DateTime>();
            //
            //    if (expiryTime > DateTime.Now) // still valid
            //    {
            //        Log("Load AppToken from cache");
            //
            //        oauthCookie = cacheData["cookie"].ToObject<Cookie>();
            //        cookies.Add(oauthCookie);
            //        return;
            //    }
            //    else
            //        Log("AppToken cache expired");
            //}


            // Cookie precondition
            // ci_device_uuid=12341234-1234-1234-1234-123412341234; mijnkpnapp=1; mijnkpnapp_locale=en; mijnkpnapp_colorscheme=light;


            var request = WebRequest.Create(appToken) as HttpWebRequest; //we know we get redirected too here, so just go there. 
            request.UserAgent = appUserAgent;
            request.CookieContainer = cookies; // Assign it some cookies 
            request.ContentType = "application/x-www-form-urlencoded";
            request.Method = "POST";



            // client_id={client_id}&code={appTokenCode}&code_verifier=&grant_type=authorization_code&redirect_uri=https://mijn.kpn.com/oauth_redirect&response_type=id_token+token

            var bytes = Encoding.ASCII.GetBytes($"client_id={client_id}&grant_type=refresh_token&refresh_token={refreshToken}&redirect_uri=https%3A%2F%2Fmijn.kpn.com%2F%23%oauth_redirect");
            request.ContentLength = bytes.Length;
            using (Stream loginStream = request.GetRequestStream())
            {
                loginStream.Write(bytes, 0, bytes.Length);
            }

            try
            {
                using (WebResponse getResponse = request.GetResponse())
                {
                    using (StreamReader sr = new StreamReader(getResponse.GetResponseStream()))
                    {
                        var result = sr.ReadToEnd();//Read logged in webpage
                        Console.WriteLine(result);

                        var res = JsonSerializer.Deserialize<DynResRefreshAppToken>(result);


                        var stream = $"{res.id_token}";

                        //{
                        //    "access_token": "qPD1GaGruXXXcZPIh88KJSJ",
                        //    "token_type": "Bearer",
                        //    "expires_in": 3600,
                        //    "refresh_token": "7H2XRXXxZ2IW",
                        //    "scope": ""
                        //}

                        authHeader = $"Bearer {(string)res.access_token}";
                        csrfSessionKey = getResponse.Headers.Get("csrf-session-key");

                        var expiresSeconds = (int)res.expires_in;
                        File.WriteAllText(AppTokenCacheFile, JsonSerializer.Serialize(new appTokenCache
                        {
                            expiry = DateTime.Now.AddSeconds(expiresSeconds),
                            accessToken = (string)res.access_token,
                            refreshToken = (string)res.refresh_token,
                            csrfSessionKey = csrfSessionKey,
                            mijnSessionId = cookies.GetCookies(new Uri("https://mijn.kpn.com/"))["sessionid"]
                        }));
                        return true;
                    }
                }
            }
            catch (System.Net.WebException exception)
            {
                Log($"GetRefreshAppToken threw: ${exception.ToString()}");
                // login again and retry
                //if (exception.Status == WebExceptionStatus.ProtocolError &&
                //    ((System.Net.HttpWebResponse)exception.Response).StatusCode == HttpStatusCode.Unauthorized)
                //{
                //
                //    GetLoginToken(cookies, out csrfSessionKey, true);
                //    GetAppInit(cookies, ref csrfSessionKey, out appTokenCode, true);
                //    GetAppToken(cookies, ref csrfSessionKey, ref appTokenCode, out oauthCookie, true);
                //}
                //else
                //throw;
                authHeader = "";
                csrfSessionKey = "";

                return false;
            }
        }


    }
}
