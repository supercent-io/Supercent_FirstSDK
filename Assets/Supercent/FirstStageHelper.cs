using Facebook.Unity;

namespace Supercent
{
    public static class FirstStageHelper
    {
        static bool isCalled = false;

        public static void Initialize()
        {
            if (isCalled)
                return;

            isCalled = true;
            InitFacebook();
            InitAnalytics();
        }

        static void InitFacebook()
        {
            if (FB.IsInitialized)
            {
                FB.ActivateApp();
            }
            else
            {
                FB.Init(() =>
                {
                    FB.ActivateApp();
                });
            }
        }

        static void InitAnalytics()
        {
            GameAnalyticsSDK.GameAnalytics.SettingsGA.UsePlayerSettingsBuildNumber = true;
            GameAnalyticsSDK.GameAnalytics.Initialize();
        }
    }
}