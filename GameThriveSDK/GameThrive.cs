using Microsoft.Phone.Controls;
using Microsoft.Phone.Info;
using Microsoft.Phone.Notification;
using Microsoft.Phone.Reactive;
using Microsoft.Phone.Shell;
using Microsoft.Phone.Tasks;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO.IsolatedStorage;
using System.Net;
using System.Windows;
using System.Xml.Linq;

using Newtonsoft.Json.Linq;
using System.Threading;

namespace GameThriveSDK {

    public class GameThrive {
        
        private const string BASE_URL = "https://gamethrive.com/";
        private static string mAppId;
        private static string mPlayerId, mChannelUri;
        private static long lastPingTime;
        private static IsolatedStorageSettings settings = IsolatedStorageSettings.ApplicationSettings;
        private static bool initDone = false;


        public delegate void NotificationReceived(IDictionary<string, string> additionalData, bool isActive);
        private static NotificationReceived notificationDelegate = null;

        public delegate void IdsAvailable(string playerID, string pushToken);
        public static IdsAvailable idsAvailableDelegate = null;

        public delegate void TagsReceived(IDictionary<string, string> tags);
        public static TagsReceived tagsReceivedDelegate = null;

        private static IDisposable fallBackGameThriveSession;

        private static bool sessionCallInProgress, sessionCallDone;

        public static void Init(string appId, NotificationReceived inNotificationDelegate = null) {
            if (initDone)
                return;

            mAppId = appId;
            notificationDelegate = inNotificationDelegate;
            mPlayerId = settings.Contains("GameThrivePlayerId") ? (string)settings["GameThrivePlayerId"] : null;
            mChannelUri = settings.Contains("GameThriveChannelUri") ? (string)settings["GameThriveChannelUri"] : null;
            
            string channelName = "GameThriveApp" + appId;
            
            var pushChannel = HttpNotificationChannel.Find(channelName);

            if (pushChannel == null) {
                pushChannel = new HttpNotificationChannel(channelName);

                SubscribeToChannelEvents(pushChannel);

                pushChannel.Open();
                pushChannel.BindToShellToast();
                fallBackGameThriveSession = new Timer((o) => Deployment.Current.Dispatcher.BeginInvoke(() => SendSession(null)), null, 20000, Timeout.Infinite);
            }
            else { // Else gets run on the 2nd open of the app and after. This happens on WP8.0 but not WP8.1
                SubscribeToChannelEvents(pushChannel);

                if (!pushChannel.IsShellToastBound)
                    pushChannel.BindToShellToast();

                // Since the channel was found ChannelUriUpdated does not fire so send an on session event.
                SendSession(null);
            }

            lastPingTime = DateTime.Now.Ticks;
            PhoneApplicationService.Current.Closing += (s, e) => SaveActiveTime();
            PhoneApplicationService.Current.Deactivated += (s, e) => SaveActiveTime();
            PhoneApplicationService.Current.Activated += AppResumed;

            // Using Disatcher due to Unity threading issues with Application.Current.RootVisual.
            // Works fine with normal native apps too.
            Deployment.Current.Dispatcher.BeginInvoke(() => {
                var startingPage = ((PhoneApplicationFrame)Application.Current.RootVisual).Content as PhoneApplicationPage;
                
                if (startingPage.NavigationContext.QueryString.ContainsKey("GameThriveParams"))
                    NotificationOpened(startingPage.NavigationContext.QueryString["GameThriveParams"]);

                SendPing(GetSavedActiveTime());

                initDone = true;
            });
        }

        private static long GetSavedActiveTime() {
            if (settings.Contains("GameThriveActiveTime"))
                return (long)settings["GameThriveActiveTime"];
            return 0;
        }

        // Save off the time the user was running your app so we can send it the next time the app is open/resumed
        // This is done as a Http call when the app is closing is not reliable and when the app is Deactivated all http requests get paused.
        private static void SaveActiveTime() {
            long timeToAdd = (DateTime.Now.Ticks - lastPingTime) / 10000000;
            if (settings.Contains("GameThriveActiveTime"))
                settings["GameThriveActiveTime"] = (long)settings["GameThriveActiveTime"] + timeToAdd;
            else
                settings.Add("GameThriveActiveTime", timeToAdd);
            settings.Save();
        }

        private static void AppResumed(object sender, ActivatedEventArgs e) {
            lastPingTime = DateTime.Now.Ticks;
            SendPing(GetSavedActiveTime());
        }

        private static void SendPing(long activeTime) {
            // Can not updated active_time if we haven't registered yet.
            // Also optimizing bandwidth by waiting until time is 10 secounds or more.
            if (mPlayerId == null || activeTime < 10)
                return;

            JObject jsonObject = JObject.FromObject(new {
                state = "ping",
                active_time = activeTime
            });

            var cli = GetWebClient();            
            cli.UploadStringCompleted += (s, e) => {
                if (!e.Cancelled && e.Error == null && settings.Contains("GameThriveActiveTime")) {
                    settings.Remove("GameThriveActiveTime");
                    settings.Save();
                }
            };
            cli.UploadStringAsync(new Uri(BASE_URL + "api/v1/players/" + mPlayerId + "/on_focus"), jsonObject.ToString());
            lastPingTime = DateTime.Now.Ticks;
        }

        private static void SubscribeToChannelEvents(HttpNotificationChannel pushChannel) {
            pushChannel.ChannelUriUpdated += new EventHandler<NotificationChannelUriEventArgs>(PushChannel_ChannelUriUpdated);
            pushChannel.ErrorOccurred += new EventHandler<NotificationChannelErrorEventArgs>(PushChannel_ErrorOccurred);
            pushChannel.ShellToastNotificationReceived += new EventHandler<NotificationEventArgs>(pushChannel_ShellToastNotificationReceived);
        }

        private static void PushChannel_ChannelUriUpdated(object sender, NotificationChannelUriEventArgs e) {
            string currentChannelUri = null;
            if (e.ChannelUri != null) {
                currentChannelUri = e.ChannelUri.ToString();
                System.Diagnostics.Debug.WriteLine("ChannelUri:" + e.ChannelUri.ToString());
            }

            SendSession(currentChannelUri);
        }

        private static void SendSession(string currentChannelUri) {
            if (sessionCallInProgress || sessionCallDone)
                return;
            
            sessionCallInProgress = true;

            string adId;
            var type = Type.GetType("Windows.System.UserProfile.AdvertisingManager, Windows, Version=255.255.255.255, Culture=neutral, PublicKeyToken=null, ContentType=WindowsRuntime");
            if (type != null)  // WP8.1 devices
                adId = (string)type.GetProperty("AdvertisingId").GetValue(null, null);
            else // WP8.0 devices, requires ID_CAP_IDENTITY_DEVICE
                adId = Convert.ToBase64String((byte[])DeviceExtendedProperties.GetValue("DeviceUniqueId"));

            if (currentChannelUri != null && mChannelUri != currentChannelUri) {
                mChannelUri = currentChannelUri;
                if (settings.Contains("GameThriveChannelUri"))
                    settings["GameThriveChannelUri"] = mChannelUri;
                else
                    settings.Add("GameThriveChannelUri", mChannelUri);
                settings.Save();
            }

            JObject jsonObject = JObject.FromObject(new {
                device_type = 3,
                app_id = mAppId,
                identifier = mChannelUri,
                ad_id = adId,
                device_model = DeviceStatus.DeviceName,
                device_os = Environment.OSVersion.Version.ToString(),
                game_version = XDocument.Load("WMAppManifest.xml").Root.Element("App").Attribute("Version").Value,
                language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToString(),
                timezone = TimeZoneInfo.Local.BaseUtcOffset.TotalSeconds.ToString()
            });

            var cli = GetWebClient();
            cli.UploadStringCompleted += (senderObj, eventArgs) => {
                sessionCallInProgress = false;
                if (eventArgs.Error == null) {
                    sessionCallDone = true;
                    if (fallBackGameThriveSession != null)
                        Deployment.Current.Dispatcher.BeginInvoke(() => { fallBackGameThriveSession.Dispose(); });

                    if (mPlayerId == null) {
                        mPlayerId = (string)JObject.Parse(eventArgs.Result)["id"];
                        settings.Add("GameThrivePlayerId", mPlayerId);
                        settings.Save();

                        if (idsAvailableDelegate != null)
                            idsAvailableDelegate(mPlayerId, mChannelUri);
                    }
                }
            };

            string urlString = BASE_URL + "api/v1/players";
            if (mPlayerId != null)
                urlString += "/" + mPlayerId + "/on_session";

            cli.UploadStringAsync(new Uri(urlString), jsonObject.ToString());
        }

        private static void pushChannel_ShellToastNotificationReceived(object sender, NotificationEventArgs e) {
            if (e.Collection.ContainsKey("wp:Param"))
                NotificationOpened(e.Collection["wp:Param"].Replace("?GameThriveParams=", ""));
        }

        private static void PushChannel_ErrorOccurred(object sender, NotificationChannelErrorEventArgs e) {
            System.Diagnostics.Debug.WriteLine("ERROR CODE:" + e.ErrorCode + ": Could not register for push notifications do to " + e.Message);
        }

        private static void NotificationOpened(string jsonParams) {
            JObject jObject = JObject.Parse(jsonParams);

            JObject jsonObject = JObject.FromObject(new {
                app_id = mAppId,
                player_id = mPlayerId,
                opened = true
            });
            
            GetWebClient().UploadStringAsync(new Uri(BASE_URL + "api/v1/notifications/" + (string)jObject["custom"]["i"]), "PUT", jsonObject.ToString());

            if (!initDone && jObject["custom"]["u"] != null) {
                WebBrowserTask webBrowserTask = new WebBrowserTask();
                webBrowserTask.Uri = new Uri((string)jObject["custom"]["u"], UriKind.Absolute);
                webBrowserTask.Show();
            }

            if (notificationDelegate != null) {
                var additionalDataJToken = jObject["custom"]["a"];
                IDictionary<string, string> additionalData = null;

                if (additionalDataJToken != null)
                    additionalData = additionalDataJToken.ToObject<Dictionary<string, string>>();

                notificationDelegate(additionalData, initDone);
            }
        }

        private static WebClient GetWebClient() {
            var webClient = new WebClient();
            webClient.Headers[HttpRequestHeader.ContentType] = "application/json";

            return webClient;
        }

        public static void SendTag(string key, string value) {
            var dictionary = new Dictionary<string, object>();
            dictionary.Add(key, value);
            SendTags((IDictionary<string, object>)dictionary);
        }

        public static void SendTags(IDictionary<string, string> keyValues) {
            SendTags((IDictionary<string, object>)keyValues);
        }

        public static void SendTags(IDictionary<string, int> keyValues) {
            SendTags((IDictionary<string, object>)keyValues);
        }

        public static void SendTags(IDictionary<string, object> keyValues) {
            if (mPlayerId == null)
                return;
            
            JObject jsonObject = JObject.FromObject(new {
                tags = keyValues
            });
            
            GetWebClient().UploadStringAsync(new Uri(BASE_URL + "api/v1/players/" + mPlayerId), "PUT", jsonObject.ToString());
        }

        public static void DeleteTags(IList<string> tags) {
            if (mPlayerId == null)
                return;

            var dictionary = new Dictionary<string, string>();
            foreach(string key in tags)
                dictionary.Add(key, "");

            JObject jsonObject = JObject.FromObject(new {
                tags = dictionary
            });
            
            GetWebClient().UploadStringAsync(new Uri(BASE_URL + "api/v1/players/" + mPlayerId), "PUT", jsonObject.ToString());
        }

        public static void DeleteTag(string tag) {
            DeleteTags(new List<string>(){tag}); 
        }

        public static void SendPurchase(double amount) {
            SendPurchase((decimal)amount);
        }

        public static void SendPurchase(decimal amount) {
            if (mPlayerId == null)
                return;

            JObject jsonObject = JObject.FromObject(new {
                amount = amount
            });

            GetWebClient().UploadStringAsync(new Uri(BASE_URL + "api/v1/players/" + mPlayerId + "/on_purchase"), jsonObject.ToString());
        }

        public static void GetIdsAvailable() {
            if (idsAvailableDelegate == null)
                throw new ArgumentNullException("Assign idsAvailableDelegate before calling or call GetIdsAvailable(IdsAvailable)");

            if (mPlayerId != null)
                idsAvailableDelegate(mPlayerId, mChannelUri);
        }

        public static void GetIdsAvailable(IdsAvailable inIdsAvailableDelegate) {
            idsAvailableDelegate = inIdsAvailableDelegate;

            if (mPlayerId != null)
                idsAvailableDelegate(mPlayerId, mChannelUri);
        }

        public static void GetTags() {
            if (mPlayerId == null)
                return;
            
            if (tagsReceivedDelegate == null)
                throw new ArgumentNullException("Assign tagsReceivedDelegate before calling or call GetTags(TagsReceived)");

            SendGetTagsMessage();
        }

        public static void GetTags(TagsReceived inTagsReceivedDelegate) {
            if (mPlayerId == null)
                return;
            
            tagsReceivedDelegate = inTagsReceivedDelegate;

            SendGetTagsMessage();
        }

        private static void SendGetTagsMessage() {
            var webClient = new WebClient();
            webClient.DownloadStringCompleted += (s, e) => {
                if (e.Error == null)
                    tagsReceivedDelegate(JObject.Parse(e.Result)["tags"].ToObject<Dictionary<string, string>>());
            };
            
            webClient.DownloadStringAsync(new Uri(BASE_URL + "api/v1/players/" + mPlayerId + ".test"));
        }
    }
}