using System;
using System.CodeDom;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace KPNAPIPoll
{
    partial class KPNRequester
    {
        const string selectPlan = "https://mijn.kpn.com/api/v5/?method=kpn.subscription.prepareForChange&sc_service_token=0E9PGEd0";
        const string buyPlan = "https://mijn.kpn.com/api/v5/?method=kpn.subscription.change&sc_service_token=0E9PGEd0";

        string AppTokenCacheFile = "apps/appToken.txt";


        private const string selfcareProducts = "https://api.kpn.com/selfcare-bff/v1/products?sc_service_types=cscl3,cscmobpost,cscmobpre";

        private CookieContainer cookies = new CookieContainer();
        private readonly IConfiguration _cfg;

        public KPNRequester(IConfiguration cfg)
        {
            _cfg = cfg;
            // Base cookies that are always present
            cookies.Add(new Cookie("mijnkpnapp", "1", "/", domain: "kpn.com"));
            cookies.Add(new Cookie("mijnkpnapp_locale", "en", "/", domain: "kpn.com"));
            cookies.Add(new Cookie("mijnkpnapp_colorscheme", "light", "/", domain: "kpn.com"));
            cookies.Add(new Cookie("mijnkpnauth", "1", "/", "mijn.kpn.com"));
            // Don't know what this is, its some MD5 hash, not the login name, maybe some device identifier. Not setting this triggers "login from new device" E-Mails to be sent
            cookies.Add(new Cookie("mijnkpnpref", $"{{\"{_cfg["KPN:SomeHashThing"]}\":{{\"mobileNumber\":\"{Convert.ToHexString(MD5.HashData(Encoding.ASCII.GetBytes(_cfg["KPN:Number"]))).ToLower()}\"}}}}", "/", "mijn.kpn.com"));
        }

        void Log(string message)
        {
            Console.WriteLine(message);
        }

        public struct ProductUsage
        {
            public string bundleType { get; set; }
            // list bundles that lists all activated ones
            public string initial { get; set; } // Available data in gb
            public string usage { get; set; } // Used data in gb
            public string region { get; set; }
            public string type { get; set; }
        }

        struct selfcareProductUsageDTO
        {
            public bool dailyUnlimitedUsed { get; set; }
            public List<ProductUsage> value { get; set; }
        }

        public struct ProductEntry
        {
            public string id { get; set; }
            public string shopContext { get; set; }
            public string subscriptionId { get; set; }

            public ProductUsage GetUsage(KPNRequester requester, bool canRetry = true)
            {
                // Cookie precondition
                // ci_device_uuid=12341234-1234-1234-1234-123412341234; mijnkpnapp=1; mijnkpnapp_locale=en; mijnkpnapp_colorscheme=light;
                // authorization bearer token

                {
                    var request = WebRequest.Create($"https://api.kpn.com/selfcare-bff/v4/products/{id}/usage?sc_service_types=cscl3,cscmobpost,cscmobpre") as HttpWebRequest;
                    request.CookieContainer = requester.cookies; // Assign it some cookies 
                    request.Method = "GET";
                    request.UserAgent = appUserAgent;
                    request.Headers.Add(HttpRequestHeader.Authorization, requester.authHeader);

                    try
                    {
                        using (WebResponse getResponse = request.GetResponse())
                        {
                            using (StreamReader sr = new StreamReader(getResponse.GetResponseStream()))
                            {
                                var result = sr.ReadToEnd();//Read logged in webpage
                                Console.WriteLine(result);

                                var res = JsonSerializer.Deserialize<selfcareProductUsageDTO>(result);

                                return res.value.First(x => x.type == "data" && x.region == "nl");
                            }
                        }
                    }
                    catch (System.Net.WebException exception)
                    {
                        requester.Log($"ProductEntry.GetUsage threw: ${exception.ToString()}");
                        using (StreamReader sr = new StreamReader(exception.Response.GetResponseStream()))
                        {
                            var result = sr.ReadToEnd();//Read logged in webpage
                            // See if it somehow indicates that token expired
                            Console.WriteLine(result);
                        }
                        // {"message":"UNAUTHORIZED_REQUEST","errors":[],"key":null,"code":"INVALID_BEARER_TOKEN","error_code":null}
                        // login again and retry
                        if (canRetry &&
                            exception.Status == WebExceptionStatus.ProtocolError &&
                            ((System.Net.HttpWebResponse)exception.Response).StatusCode == HttpStatusCode.Unauthorized)
                        {
                            requester.DoLogin();
                            return GetUsage(requester, false);
                        }
                        else
                            throw;
                    }
                }
            }

        }

        struct ProductCategory
        {
            public string category { get; set; }
            public List<ProductEntry> products { get; set; }
            public int status { get; set; }
        }

        struct selfcareProductsDTO
        {
            public List<ProductCategory> value {get; set; }
        }


        public ProductEntry GetProducts()
        {

            // Cookie precondition
            // ci_device_uuid=12341234-1234-1234-1234-123412341234; mijnkpnapp=1; mijnkpnapp_locale=en; mijnkpnapp_colorscheme=light;
            // authorization bearer token

            {
                var request = WebRequest.Create(selfcareProducts) as HttpWebRequest;
                request.CookieContainer = cookies; // Assign it some cookies 
                request.Method = "GET";
                request.UserAgent = appUserAgent;
                //request.Proxy = new WebProxy("127.0.0.1:8080");
                request.Headers.Add(HttpRequestHeader.Authorization, authHeader);

                try
                {
                    using (WebResponse getResponse = request.GetResponse())
                    {
                        using (StreamReader sr = new StreamReader(getResponse.GetResponseStream()))
                        {
                            var result = sr.ReadToEnd();//Read logged in webpage
                            Console.WriteLine(result);

                            var res = JsonSerializer.Deserialize<selfcareProductsDTO>(result);

                            var category = res.value.First(x => x.products.Count != 0);
                            var product = category.products.First();
                            // I know I only have one I care about

                            return product;
                        }
                    }
                }
                catch (System.Net.WebException exception)
                {
                    Log($"GetProducts threw: ${exception.ToString()}");
                    throw;
                }
            }
        }

        public bool OrderPacket(bool canRetry = true)
        {
            #region Select sub plan
            var request = WebRequest.Create(selectPlan) as HttpWebRequest; //we know we get redirected to here, so just go there. 
                request.UserAgent = cigatewayUserAgent;
                request.CookieContainer = cookies; // Assign it some cookies 
                request.ContentType = "application/x-www-form-urlencoded";
                request.Method = "POST";
                request.Headers.Add("Origin", "https://mijn.kpn.com");
                request.Referer = "https://mijn.kpn.com/";

                request.Headers.Add("x-requested-with", "nl.kpn.mijn");
                request.Headers.Add("app-identifier", "mijn_kpn_desktop");
                request.Headers.Add("CSRF-Session-Key", csrfSessionKey);


                var bytes = Encoding.ASCII.GetBytes($"number={_cfg["KPN:Number"]}&subscriptionPlanId={_cfg["KPN:SubscriptionPlanId"]}&type=addon"); // 2GB free
                request.ContentLength = bytes.Length;
                using (Stream loginStream = request.GetRequestStream())
                {
                    loginStream.Write(bytes, 0, bytes.Length);
                }

                try
                {
                    request.GetResponse().Dispose();
                }
                catch (Exception ex)
                {
                    Log($"Select subplan threw: ${ex.ToString()}");


                    if (ex is System.Net.WebException exception)
                    {
                        // login again and retry
                        if (canRetry &&
                            exception.Status == WebExceptionStatus.ProtocolError &&
                            ((System.Net.HttpWebResponse)exception.Response).StatusCode == HttpStatusCode.Unauthorized)
                        {
                            DoLogin();
                            return OrderPacket(false);
                        }
                        else if (exception.Status == WebExceptionStatus.ProtocolError &&
                                 ((System.Net.HttpWebResponse) exception.Response).StatusCode == HttpStatusCode.BadRequest)
                        {
                            return false;
                        }
                        else
                            throw;
                    }
                    else
                        throw;

                    return false;
                }

                // {
                //     "status": "OK",
                //     "warningTitle": null,
                //     "warning": null,
                //     "messageTitle": null,
                //     "messages": null
                // }
            #endregion

            #region buy
                request = WebRequest.Create(buyPlan) as HttpWebRequest; //we know we get redirected too here, so just go there. 
                request.UserAgent = "Mozilla/5.0 (Windows NT 6.1; WOW64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/43.0.2357.134 Safari/537.36";
                request.CookieContainer = cookies; // Assign it some cookies 
                request.ContentType = "application/x-www-form-urlencoded";
                request.Method = "POST";
                request.Headers.Add("Origin", "https://mijn.kpn.com");
                request.Referer = "https://mijn.kpn.com/";

                request.Headers.Add("x-requested-with", "nl.kpn.mijn");
                request.Headers.Add("app-identifier", "mijn_kpn_desktop");
                request.Headers.Add("CSRF-Session-Key", csrfSessionKey);

                bytes = Encoding.ASCII.GetBytes($"number={_cfg["KPN:Number"]}");
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
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"Buy threw: ${ex.ToString()}");

                    if (ex is System.Net.WebException exception)
                    {

                        // login again and retry
                        //if (exception.Status == WebExceptionStatus.ProtocolError &&
                        //    ((System.Net.HttpWebResponse)exception.Response).StatusCode == HttpStatusCode.Unauthorized)
                        //{
                        //
                        //    GetLoginToken(cookies, out csrfSessionKey, true);
                        //    GetAppInit(cookies, ref csrfSessionKey, out var appTokenCode, true);
                        //    GetAppToken(cookies, ref csrfSessionKey, ref appTokenCode, out var oauthCookie, true);
                        //    return OrderPacket(cookies, ref csrfSessionKey);
                        //}
                        //else if (exception.Status == WebExceptionStatus.ProtocolError &&
                        //         ((System.Net.HttpWebResponse)exception.Response).StatusCode ==
                        //         HttpStatusCode.BadRequest)
                        //{
                        //    return false;
                        //}
                        //else
                            throw;
                    }
                    else
                        throw;

                    return false;
                }
            // {
            //     "status": "OK",
            //     "messages": [
            //         "Bedankt voor uw bestelling. Het kan een uur duren voordat deze wijziging zichtbaar is in MijnKPN."
            //     ]
            // }


            return true;

            #endregion
        }
    }
}
