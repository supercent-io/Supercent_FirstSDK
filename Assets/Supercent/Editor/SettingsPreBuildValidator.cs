using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Supercent.Edit
{
    /// <summary>
    /// Runs First Stage Settings validation immediately before Unity starts a player build.
    /// Throwing BuildFailedException stops the build with a clear error message.
    /// </summary>
    sealed class SettingsPreBuildValidator : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            var options = new SettingsValidationHelper.ValidationOptions
            {
                ValidateFacebook = true,
                ValidateGameAnalytics = true,
                GameAnalyticsMode = SettingsValidationHelper.GameAnalyticsPlatformValidationMode.BuildTargetOnly,
                BuildTarget = report.summary.platform
            };

            SettingsValidationHelper.ValidationResult result =
                SettingsValidationHelper.ValidateAllAndWait(
                    options,
                    "Validating First Stage Settings Before Build",
                    false
                );

            if (result.IsValid)
            {
                Debug.Log("[First Stage Settings] Pre-build validation passed.\n" + result.ToDisplayString());
                return;
            }

            string message = result.ToBuildFailureMessage();
            Debug.LogError(message);
            throw new BuildFailedException(message);
        }
    }
}
