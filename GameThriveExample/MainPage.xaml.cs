using Microsoft.Phone.Controls;

using System.Collections.Generic;
using System.Windows;
using System.Windows.Navigation;
using System.Linq;

using GameThriveSDK;

namespace GameThriveExample {
    public partial class MainPage : PhoneApplicationPage {
        
        public MainPage() {
            InitializeComponent();
            SendTagsButton.Click += SendTagsButton_Click;
            SendPurchaseButton.Click += SendPurchaseButton_Click;
        }

        void SendPurchaseButton_Click(object sender, RoutedEventArgs e) {
            GameThrive.SendPurchase(1.99);
        }

        void SendTagsButton_Click(object sender, RoutedEventArgs e) {
            GameThrive.SendTag("WPKey", "WPValue");
        }

        protected override void OnNavigatedTo(NavigationEventArgs navEventArgs) {
            base.OnNavigatedTo(navEventArgs);

            GameThrive.Init("5eb5a37e-b458-11e3-ac11-000c2940e62c", ReceivedNotification);
        }

        // Called when the user opens a notification or one comes in while using the app.
        // The name of the method can be anything as long as the signature matches.
        // Method must be static or be in a class where the same instance stays alive with the app.
        private static void ReceivedNotification(IDictionary<string, string> additionalData, bool isActive) {
            if (additionalData != null)
                System.Diagnostics.Debug.WriteLine("additionalData:\n" + string.Join(";", additionalData.Select(x => x.Key + "=" + x.Value).ToArray()));
        }
    }
}