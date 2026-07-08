using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using UnityEditor;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace Supercent.Edit
{
    /// <summary>
    /// Shared validation entry point for FirstStageSettings UI and pre-build validation.
    ///
    /// Design notes:
    /// - Unity assets are read synchronously on the editor/main thread into plain snapshots.
    /// - Async validation runs only against plain data, so future HTTP/API calls can be added safely.
    /// - Synchronous callers can still wait for the async result through the *AndWait methods.
    /// </summary>
    public static class SettingsValidationHelper
    {
        public const string FacebookDefaultPath = "Assets/FacebookSDK/SDK/Resources/FacebookSettings.asset";
        public const string GameAnalyticsDefaultPath = "Assets/Resources/GameAnalytics/Settings.asset";

        public const string FirstStepValidateApiPath = "/api/v1/firststep/validate";
        public const string FirstStepBuildRegisterApiPath = "/api/v1/firststep/build/register";
        public const string FirstStepSdkVersion = "1.0.0";

        private const string FirstStepResultPass = "pass";
        private const string FirstStepResultAttention = "attention";
        private const string FirstStepValidateResponseSource = "validate";
        private const string FirstStepBuildRegisterResponseSource = "build_register";
        private const string AndroidXmlNamespace = "http://schemas.android.com/apk/res/android";
        private const string AndroidFacebookApplicationIdMetadataName = "com.facebook.sdk.ApplicationId";
        private const string AndroidFacebookClientTokenMetadataName = "com.facebook.sdk.ClientToken";

        // Configure these project-wide values before running build validation.
        // Example: FirstStepValidateApiDomain = "https://api.example.com";
        public static string FirstStepValidateApiDomain = "https://cpi-dev-backend.supercent.net";
        public static string FirstStepValidateApiKey = "eceed79e3ec1cd3ffe0a83329a621c7157d5b7c3b24fafa2";
        public static int FirstStepValidateApiTimeoutSeconds = 15;

        private const string FacebookSettingsTypeName = "Facebook.Unity.Settings.FacebookSettings";
        private const string GameAnalyticsSettingsTypeName = "GameAnalyticsSDK.Setup.Settings";
        private const int MaxApiResponseBodyLogLength = 2000;

        private static readonly string[] FacebookSettingsMenuPaths =
        {
            "Facebook/Edit Settings"
        };

        private static readonly string[] GameAnalyticsSettingsMenuPaths =
        {
            "Window/GameAnalytics/Select Settings"
        };

        private static readonly string[] FacebookAndroidManifestCandidatePaths =
        {
            "Assets/Plugins/Android/AndroidManifest.xml",
            "Assets/Plugins/Android/AndroidManifest"
        };

        private static readonly string[] FacebookAndroidStringResourceRootPaths =
        {
            "Assets/Plugins/Android",
            "Assets/FacebookSDK/Plugins/Android"
        };

        public enum ValidationSeverity
        {
            Info,
            Warning,
            Error
        }

        public enum GameAnalyticsPlatformValidationMode
        {
            AllMobilePlatforms,
            BuildTargetOnly
        }

        public enum FirstStepApiEndpoint
        {
            Validate,
            BuildRegister
        }

        public sealed class ValidationMessage
        {
            public ValidationSeverity Severity;
            public string Source;
            public string Message;

            public ValidationMessage(ValidationSeverity severity, string source, string message)
            {
                Severity = severity;
                Source = source;
                Message = message;
            }
        }

        public sealed class ValidationResult
        {
            private readonly List<ValidationMessage> messages = new List<ValidationMessage>();

            public IReadOnlyList<ValidationMessage> Messages => messages;

            public bool HasErrors
            {
                get
                {
                    for (int i = 0; i < messages.Count; i++)
                    {
                        if (messages[i].Severity == ValidationSeverity.Error)
                        {
                            return true;
                        }
                    }

                    return false;
                }
            }

            public bool IsValid => !HasErrors;

            public void AddInfo(string source, string message) => Add(ValidationSeverity.Info, source, message);
            public void AddWarning(string source, string message) => Add(ValidationSeverity.Warning, source, message);
            public void AddError(string source, string message) => Add(ValidationSeverity.Error, source, message);

            public void Add(ValidationSeverity severity, string source, string message)
            {
                messages.Add(new ValidationMessage(severity, source, message));
            }

            public void Merge(ValidationResult other)
            {
                if (other == null)
                {
                    return;
                }

                for (int i = 0; i < other.messages.Count; i++)
                {
                    messages.Add(other.messages[i]);
                }
            }

            public string ToDisplayString()
            {
                var sb = new StringBuilder();
                sb.AppendLine(IsValid ? "Validation passed." : "Validation failed.");

                if (messages.Count == 0)
                {
                    return sb.ToString();
                }

                sb.AppendLine();

                for (int i = 0; i < messages.Count; i++)
                {
                    ValidationMessage message = messages[i];
                    sb.Append("[");
                    sb.Append(message.Severity);
                    sb.Append("] ");

                    if (!string.IsNullOrEmpty(message.Source))
                    {
                        sb.Append(message.Source);
                        sb.Append(" - ");
                    }

                    sb.AppendLine(message.Message);
                }

                return sb.ToString();
            }

            public string ToBuildFailureMessage()
            {
                var sb = new StringBuilder();
                sb.AppendLine("First Stage Settings validation failed before build.");
                sb.AppendLine();

                bool wroteError = false;

                for (int i = 0; i < messages.Count; i++)
                {
                    ValidationMessage message = messages[i];
                    if (message.Severity != ValidationSeverity.Error)
                    {
                        continue;
                    }

                    wroteError = true;
                    sb.Append("- ");

                    if (!string.IsNullOrEmpty(message.Source))
                    {
                        sb.Append(message.Source);
                        sb.Append(": ");
                    }

                    sb.AppendLine(message.Message);
                }

                if (!wroteError)
                {
                    sb.AppendLine("- Unknown validation error.");
                }

                return sb.ToString();
            }

            public static ValidationResult FromException(Exception exception)
            {
                var result = new ValidationResult();
                result.AddError("Validation", exception == null ? "Unknown validation exception." : exception.Message);
                return result;
            }
        }

        public sealed class ValidationOptions
        {
            public UnityObject FacebookSettings;
            public UnityObject GameAnalyticsSettings;

            public bool ValidateFacebook = true;
            public bool ValidateGameAnalytics = true;
            public bool ValidatePlayerBuildSettings;
            public bool ValidateFirstStepApi;

            public GameAnalyticsPlatformValidationMode GameAnalyticsMode = GameAnalyticsPlatformValidationMode.AllMobilePlatforms;
            public FirstStepApiEndpoint FirstStepEndpoint = FirstStepApiEndpoint.Validate;
            public BuildTarget? BuildTarget;
        }

        private sealed class PlayerBuildSettingsSnapshot
        {
            public bool HasBuildTarget;
            public bool IsSupportedMobileBuildTarget;
            public BuildTarget BuildTarget;
            public BuildTargetGroup BuildTargetGroup;
            public string PlatformLabel;
            public string Os;
            public string PackageName;
            public string GameVersion;
            public string BundleVersionCode;
        }

        private sealed class FacebookSnapshot
        {
            public UnityObject Asset;
            public string AssetPath;
            public bool HasAsset;
            public bool HasRequiredFields;
            public int SelectedAppIndex;
            public int AppCount;
            public string AppName;
            public string AppId;
            public string ClientToken;

            public bool UsesPlatformFile;
            public bool HasPlatformFile;
            public BuildTarget BuildTarget;
            public string PlatformLabel;
            public string PlatformFilePath;
            public string PlatformFileReadError;
            public bool AppIdEntryExists;
            public bool ClientTokenEntryExists;
            public string AppIdRawValue;
            public string ClientTokenRawValue;
            public string AppIdResolutionError;
            public string ClientTokenResolutionError;
        }

        private sealed class GameAnalyticsSnapshot
        {
            public UnityObject Asset;
            public string AssetPath;
            public bool HasAsset;
            public bool HasRequiredFields;
            public int PlatformCount;
            public readonly Dictionary<RuntimePlatform, GameAnalyticsPlatformSnapshot> Platforms =
                new Dictionary<RuntimePlatform, GameAnalyticsPlatformSnapshot>();
        }

        private sealed class GameAnalyticsPlatformSnapshot
        {
            public bool Exists;
            public int Index = -1;
            public bool GameKeyEntryExists;
            public bool SecretKeyEntryExists;
            public string GameKey;
            public string SecretKey;
        }

        [Serializable]
        private sealed class FirstStepValidateRequest
        {
            public string packageName;
            public string os;
            public string gaGameKey;
            public string gaSecretKey;
            public string fbAppId;
            public string fbClientToken;
            public string gameVersion;
            public string bundleVersionCode;
            public string sdkVersion;
        }

        [Serializable]
        private sealed class FirstStepValidateResponse
        {
            public string status;
            public string message;
            public FirstStepValidateResponseData data;
        }

        [Serializable]
        private sealed class FirstStepValidateResponseData
        {
            public bool valid;
            public string status;
            public string source;
            public string checkedAt;
            public FirstStepValidateChecks checks;
            public FirstStepValidateError[] errors;
        }

        [Serializable]
        private sealed class FirstStepValidateChecks
        {
            public string packageName;
            public string gaGameKey;
            public string gaSecretKey;
            public string fbAppId;
            public string fbClientToken;
            public string keyHash;
        }

        [Serializable]
        private sealed class FirstStepValidateError
        {
            public string field;
            public string code;
            public string message;
        }

        public static UnityObject FindFacebookSettingsAsset()
        {
            return FindAssetByPathOrType(
                FacebookDefaultPath,
                "FacebookSettings",
                FacebookSettingsTypeName
            );
        }

        public static UnityObject FindGameAnalyticsSettingsAsset()
        {
            return FindAssetByPathOrType(
                GameAnalyticsDefaultPath,
                "Settings",
                GameAnalyticsSettingsTypeName
            );
        }

        public static bool OpenFacebookSettingsMenu()
        {
            return ExecuteFirstAvailableMenuItem(FacebookSettingsMenuPaths);
        }

        public static bool OpenGameAnalyticsSettingsMenu()
        {
            return ExecuteFirstAvailableMenuItem(GameAnalyticsSettingsMenuPaths);
        }

        public static string GetFacebookSettingsMenuPathLabel()
        {
            return JoinMenuPathLabels(FacebookSettingsMenuPaths);
        }

        public static string GetGameAnalyticsSettingsMenuPathLabel()
        {
            return JoinMenuPathLabels(GameAnalyticsSettingsMenuPaths);
        }

        public static void SaveAndRefreshAssets()
        {
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        public static Task<ValidationResult> ValidateAllAsync(CancellationToken cancellationToken = default)
        {
            return ValidateAllAsync(new ValidationOptions(), cancellationToken);
        }

        public static async Task<ValidationResult> ValidateAllAsync(
            ValidationOptions options,
            CancellationToken cancellationToken = default)
        {
            options = options ?? new ValidationOptions();

            UnityObject facebookAsset = options.FacebookSettings != null
                ? options.FacebookSettings
                : FindFacebookSettingsAsset();

            UnityObject gameAnalyticsAsset = options.GameAnalyticsSettings != null
                ? options.GameAnalyticsSettings
                : FindGameAnalyticsSettingsAsset();

            FacebookSnapshot facebookSnapshot = (options.ValidateFacebook || options.ValidateFirstStepApi)
                ? ReadFacebookSnapshot(facebookAsset, options.BuildTarget)
                : null;

            GameAnalyticsSnapshot gameAnalyticsSnapshot = (options.ValidateGameAnalytics || options.ValidateFirstStepApi)
                ? ReadGameAnalyticsSnapshot(gameAnalyticsAsset)
                : null;

            PlayerBuildSettingsSnapshot playerBuildSettingsSnapshot =
                (options.ValidatePlayerBuildSettings || options.ValidateFirstStepApi)
                    ? ReadPlayerBuildSettingsSnapshot(options.BuildTarget)
                    : null;

            var result = new ValidationResult();

            if (options.ValidateFacebook && facebookSnapshot != null)
            {
                result.Merge(await ValidateFacebookSnapshotAsync(facebookSnapshot, cancellationToken).ConfigureAwait(false));
            }

            if (options.ValidateGameAnalytics && gameAnalyticsSnapshot != null)
            {
                result.Merge(await ValidateGameAnalyticsSnapshotAsync(
                    gameAnalyticsSnapshot,
                    options.GameAnalyticsMode,
                    options.BuildTarget,
                    cancellationToken
                ).ConfigureAwait(false));
            }

            if (options.ValidatePlayerBuildSettings && playerBuildSettingsSnapshot != null)
            {
                result.Merge(ValidatePlayerBuildSettingsSnapshot(playerBuildSettingsSnapshot));
            }

            if (options.ValidateFirstStepApi)
            {
                if (result.HasErrors)
                {
                    result.AddInfo("FirstStep API", "Remote API " + GetFirstStepApiOperationLabel(options.FirstStepEndpoint) + " was skipped because local validation failed.");
                }
                else
                {
                    result.Merge(await ValidateFirstStepApiAsync(
                        facebookSnapshot,
                        gameAnalyticsSnapshot,
                        playerBuildSettingsSnapshot,
                        options.FirstStepEndpoint,
                        cancellationToken
                    ).ConfigureAwait(false));
                }
            }

            return result;
        }

        public static async Task<ValidationResult> ValidateFacebookAsync(
            UnityObject facebookSettings,
            CancellationToken cancellationToken = default)
        {
            FacebookSnapshot snapshot = ReadFacebookSnapshot(facebookSettings, null);
            return await ValidateFacebookSnapshotAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }

        public static async Task<ValidationResult> ValidateGameAnalyticsAsync(
            UnityObject gameAnalyticsSettings,
            GameAnalyticsPlatformValidationMode mode = GameAnalyticsPlatformValidationMode.AllMobilePlatforms,
            BuildTarget? buildTarget = null,
            CancellationToken cancellationToken = default)
        {
            GameAnalyticsSnapshot snapshot = ReadGameAnalyticsSnapshot(gameAnalyticsSettings);
            return await ValidateGameAnalyticsSnapshotAsync(snapshot, mode, buildTarget, cancellationToken).ConfigureAwait(false);
        }

        public static ValidationResult ValidateAllAndWait(
            ValidationOptions options,
            string progressTitle = "Validating First Stage Settings",
            bool allowCancel = false)
        {
            return WaitForValidationResult(
                ctsToken => ValidateAllAsync(options, ctsToken),
                progressTitle,
                allowCancel
            );
        }

        public static ValidationResult ValidateFacebookAndWait(
            UnityObject facebookSettings,
            string progressTitle = "Validating Facebook Settings",
            bool allowCancel = true)
        {
            return WaitForValidationResult(
                ctsToken => ValidateFacebookAsync(facebookSettings, ctsToken),
                progressTitle,
                allowCancel
            );
        }

        public static ValidationResult ValidateGameAnalyticsAndWait(
            UnityObject gameAnalyticsSettings,
            GameAnalyticsPlatformValidationMode mode = GameAnalyticsPlatformValidationMode.AllMobilePlatforms,
            BuildTarget? buildTarget = null,
            string progressTitle = "Validating GameAnalytics Settings",
            bool allowCancel = true)
        {
            return WaitForValidationResult(
                ctsToken => ValidateGameAnalyticsAsync(gameAnalyticsSettings, mode, buildTarget, ctsToken),
                progressTitle,
                allowCancel
            );
        }

        public static ValidationResult WaitForValidationResult(
            Func<CancellationToken, Task<ValidationResult>> validationFactory,
            string progressTitle,
            bool allowCancel)
        {
            if (validationFactory == null)
            {
                var result = new ValidationResult();
                result.AddError("Validation", "Validation factory is null.");
                return result;
            }

            using (var cts = new CancellationTokenSource())
            {
                Task<ValidationResult> task;

                try
                {
                    task = validationFactory(cts.Token);
                }
                catch (Exception ex)
                {
                    return ValidationResult.FromException(ex);
                }

                try
                {
                    while (!task.IsCompleted)
                    {
                        if (!Application.isBatchMode)
                        {
                            bool canceled = allowCancel
                                ? EditorUtility.DisplayCancelableProgressBar(
                                    progressTitle,
                                    "Waiting for validation result...",
                                    0.5f
                                )
                                : false;

                            if (!allowCancel)
                            {
                                EditorUtility.DisplayProgressBar(
                                    progressTitle,
                                    "Waiting for validation result...",
                                    0.5f
                                );
                            }

                            if (canceled)
                            {
                                cts.Cancel();
                            }
                        }

                        if (cts.IsCancellationRequested)
                        {
                            break;
                        }

                        Thread.Sleep(50);
                    }

                    if (cts.IsCancellationRequested)
                    {
                        var canceledResult = new ValidationResult();
                        canceledResult.AddError("Validation", "Validation was canceled.");
                        return canceledResult;
                    }

                    return task.GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    return ValidationResult.FromException(ex);
                }
                finally
                {
                    if (!Application.isBatchMode)
                    {
                        EditorUtility.ClearProgressBar();
                    }
                }
            }
        }

        public static void ShowResultDialog(string title, ValidationResult result)
        {
            result = result ?? ValidationResult.FromException(new Exception("Validation result is null."));

            string message = result.ToDisplayString();
            string dialogTitle = title + (result.IsValid ? " - Success" : " - Failed");

            if (result.IsValid)
            {
                Debug.Log("[First Stage Settings] " + message);
                EditorUtility.DisplayDialog(dialogTitle, message, "OK");
            }
            else
            {
                Debug.LogError("[First Stage Settings] " + message);
                EditorUtility.DisplayDialog(dialogTitle, message, "OK");
            }
        }

        private static async Task<ValidationResult> ValidateFacebookSnapshotAsync(
            FacebookSnapshot snapshot,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ValidationResult result = snapshot != null && snapshot.UsesPlatformFile
                ? ValidateFacebookPlatformFileSnapshot(snapshot)
                : ValidateFacebookAssetSnapshot(snapshot);

            // Keep this method async so future remote Facebook checks can be added without changing callers.
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);

            return result;
        }

        private static ValidationResult ValidateFacebookAssetSnapshot(FacebookSnapshot snapshot)
        {
            var result = new ValidationResult();

            if (snapshot == null || !snapshot.HasAsset)
            {
                result.AddError("Facebook", "FacebookSettings.asset was not found.");
                return result;
            }

            if (!snapshot.HasRequiredFields)
            {
                result.AddError("Facebook", "Required serialized fields were not found: appLabels, appIds, clientTokens.");
                return result;
            }

            if (snapshot.AppCount <= 0)
            {
                result.AddError("Facebook", "No Facebook app entry exists.");
                return result;
            }

            if (string.IsNullOrWhiteSpace(snapshot.AppName))
            {
                result.AddError("Facebook", "Facebook App Name is empty.");
            }

            if (string.IsNullOrWhiteSpace(snapshot.AppId) || snapshot.AppId == "0")
            {
                result.AddError("Facebook", "Facebook App ID is empty or still set to 0.");
            }
            else if (!IsDigitsOnly(snapshot.AppId))
            {
                result.AddError("Facebook", "Facebook App ID should contain digits only.");
            }

            if (string.IsNullOrWhiteSpace(snapshot.ClientToken))
            {
                result.AddError("Facebook", "Facebook Client Token is empty.");
            }

            if (result.IsValid)
            {
                if (string.Equals(snapshot.PlatformLabel, "iOS", StringComparison.OrdinalIgnoreCase))
                {
                    result.AddInfo(
                        "Facebook",
                        "FacebookSettings.asset values look valid for iOS. iOS Info.plist is generated during the iOS build, so pre-build validation uses the saved FacebookSettings.asset values."
                    );
                }
                else
                {
                    result.AddInfo("Facebook", "FacebookSettings.asset values look valid.");
                }
            }

            return result;
        }

        private static ValidationResult ValidateFacebookPlatformFileSnapshot(FacebookSnapshot snapshot)
        {
            var result = new ValidationResult();
            string source = "Facebook " + (string.IsNullOrWhiteSpace(snapshot.PlatformLabel) ? snapshot.BuildTarget.ToString() : snapshot.PlatformLabel);

            if (!snapshot.HasPlatformFile)
            {
                result.AddError(source, BuildMissingFacebookPlatformFileMessage(snapshot));
                return result;
            }

            if (!string.IsNullOrWhiteSpace(snapshot.PlatformFileReadError))
            {
                result.AddError(source, "Failed to read Facebook actual settings file '" + snapshot.PlatformFilePath + "': " + snapshot.PlatformFileReadError);
                return result;
            }

            if (!snapshot.AppIdEntryExists)
            {
                result.AddError(source, GetFacebookAppIdEntryLabel(snapshot.BuildTarget) + " is missing in actual file: " + snapshot.PlatformFilePath);
            }
            else if (!string.IsNullOrWhiteSpace(snapshot.AppIdResolutionError))
            {
                result.AddError(source, snapshot.AppIdResolutionError);
            }
            else
            {
                ValidateFacebookPlatformAppId(snapshot, result, source);
            }

            if (!snapshot.ClientTokenEntryExists)
            {
                result.AddError(source, GetFacebookClientTokenEntryLabel(snapshot.BuildTarget) + " is missing in actual file: " + snapshot.PlatformFilePath);
            }
            else if (!string.IsNullOrWhiteSpace(snapshot.ClientTokenResolutionError))
            {
                result.AddError(source, snapshot.ClientTokenResolutionError);
            }
            else if (string.IsNullOrWhiteSpace(snapshot.ClientToken))
            {
                result.AddError(source, "Facebook Client Token is empty in actual file: " + snapshot.PlatformFilePath);
            }

            if (result.IsValid)
            {
                result.AddInfo(source, "Facebook actual settings file values look valid. Source: " + snapshot.PlatformFilePath);
            }

            return result;
        }

        private static void ValidateFacebookPlatformAppId(
            FacebookSnapshot snapshot,
            ValidationResult result,
            string source)
        {
            if (snapshot == null || result == null)
            {
                return;
            }

            string appId = string.IsNullOrWhiteSpace(snapshot.AppId) ? string.Empty : snapshot.AppId.Trim();
            if (string.IsNullOrEmpty(appId) || appId == "0" || string.Equals(appId, "fb0", StringComparison.OrdinalIgnoreCase))
            {
                result.AddError(source, "Facebook App ID is empty or still set to 0 in actual file: " + snapshot.PlatformFilePath);
                return;
            }

            if (snapshot.BuildTarget == BuildTarget.Android)
            {
                if (IsAndroidFacebookApplicationId(appId) || IsDigitsOnly(appId))
                {
                    if (IsDigitsOnly(appId))
                    {
                        result.AddWarning(
                            source,
                            "AndroidManifest Facebook ApplicationId usually uses the 'fb{appId}' value. Current value is digits only: " + appId
                        );
                    }

                    return;
                }

                result.AddError(
                    source,
                    "AndroidManifest Facebook ApplicationId should be 'fb' followed by digits, or a numeric app id. Current value: " + appId
                );
                return;
            }
        }

        private static async Task<ValidationResult> ValidateGameAnalyticsSnapshotAsync(
            GameAnalyticsSnapshot snapshot,
            GameAnalyticsPlatformValidationMode mode,
            BuildTarget? buildTarget,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = new ValidationResult();
            RuntimePlatform[] requiredPlatforms = GetRequiredGameAnalyticsPlatforms(mode, buildTarget);

            if (requiredPlatforms.Length == 0)
            {
                string targetLabel = buildTarget.HasValue ? buildTarget.Value.ToString() : "Unknown";
                result.AddInfo("GameAnalytics", "No target-specific GameAnalytics platform validation is required for build target: " + targetLabel);
                return result;
            }

            if (snapshot == null || !snapshot.HasAsset)
            {
                result.AddError("GameAnalytics", "GameAnalytics Settings.asset was not found.");
                return result;
            }

            if (!snapshot.HasRequiredFields)
            {
                result.AddError("GameAnalytics", "Required serialized fields were not found: gameKey, secretKey, Platforms.");
                return result;
            }

            for (int i = 0; i < requiredPlatforms.Length; i++)
            {
                RuntimePlatform platform = requiredPlatforms[i];
                string platformLabel = GetGameAnalyticsPlatformLabel(platform);
                string source = GetGameAnalyticsPlatformSource(platform);

                GameAnalyticsPlatformSnapshot platformSnapshot;
                if (!snapshot.Platforms.TryGetValue(platform, out platformSnapshot) || platformSnapshot == null || !platformSnapshot.Exists)
                {
                    result.AddError(source, platformLabel + " platform entry is missing.");
                    continue;
                }

                if (!platformSnapshot.GameKeyEntryExists)
                {
                    result.AddError(source, platformLabel + " Game Key entry is missing.");
                }
                else if (string.IsNullOrWhiteSpace(platformSnapshot.GameKey))
                {
                    result.AddError(source, platformLabel + " Game Key is empty.");
                }

                if (!platformSnapshot.SecretKeyEntryExists)
                {
                    result.AddError(source, platformLabel + " Secret Key entry is missing.");
                }
                else if (string.IsNullOrWhiteSpace(platformSnapshot.SecretKey))
                {
                    result.AddError(source, platformLabel + " Secret Key is empty.");
                }
            }

            // Future API validation entry point.
            // Example: call GameAnalytics organization/game API here.
            // Keep ConfigureAwait(false) in future awaits to avoid deadlocks when build validation waits synchronously.
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);

            if (result.IsValid)
            {
                result.AddInfo("GameAnalytics", "GameAnalytics settings look valid.");
            }

            return result;
        }

        private static ValidationResult ValidatePlayerBuildSettingsSnapshot(PlayerBuildSettingsSnapshot snapshot)
        {
            var result = new ValidationResult();

            if (snapshot == null || !snapshot.HasBuildTarget)
            {
                result.AddError("Player Settings", "Build target is not specified.");
                return result;
            }

            string source = "Player Settings " + snapshot.PlatformLabel;

            if (!snapshot.IsSupportedMobileBuildTarget)
            {
                result.AddError(source, "Only Android and iOS build settings can be validated by First Stage Settings.");
                return result;
            }

            if (string.IsNullOrWhiteSpace(snapshot.PackageName))
            {
                result.AddError(source, snapshot.PlatformLabel + " package name / bundle identifier is empty.");
            }

            if (string.IsNullOrWhiteSpace(snapshot.GameVersion))
            {
                result.AddError(source, "Game version is empty.");
            }

            if (string.IsNullOrWhiteSpace(snapshot.BundleVersionCode))
            {
                result.AddError(source, snapshot.PlatformLabel + " bundle version code / build number is empty.");
            }
            else if (snapshot.BuildTarget == BuildTarget.Android)
            {
                int androidVersionCode;
                if (!int.TryParse(snapshot.BundleVersionCode, out androidVersionCode) || androidVersionCode <= 0)
                {
                    result.AddError(source, "Android bundle version code must be a positive integer.");
                }
            }

            if (result.IsValid)
            {
                result.AddInfo(source, snapshot.PlatformLabel + " player build settings look valid.");
            }

            return result;
        }

        private static async Task<ValidationResult> ValidateFirstStepApiAsync(
            FacebookSnapshot facebookSnapshot,
            GameAnalyticsSnapshot gameAnalyticsSnapshot,
            PlayerBuildSettingsSnapshot playerBuildSettingsSnapshot,
            FirstStepApiEndpoint endpoint,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = new ValidationResult();

            Uri uri;
            if (!TryBuildFirstStepApiUri(endpoint, out uri))
            {
                result.AddError("FirstStep API", "FirstStepValidateApiDomain is empty or invalid.");
                return result;
            }

            string apiPath = GetFirstStepApiPath(endpoint);
            string operationLabel = GetFirstStepApiOperationLabel(endpoint);

            if (string.IsNullOrWhiteSpace(FirstStepValidateApiKey))
            {
                result.AddError("FirstStep API", "FirstStepValidateApiKey is empty.");
                return result;
            }

            FirstStepValidateRequest request = CreateFirstStepValidateRequest(
                facebookSnapshot,
                gameAnalyticsSnapshot,
                playerBuildSettingsSnapshot,
                result
            );

            if (!result.IsValid || request == null)
            {
                return result;
            }

            string requestJson = JsonUtility.ToJson(request);

            try
            {
                using (var httpClient = new HttpClient())
                using (var httpRequest = new HttpRequestMessage(HttpMethod.Post, uri))
                {
                    int timeoutSeconds = FirstStepValidateApiTimeoutSeconds <= 0
                        ? 15
                        : FirstStepValidateApiTimeoutSeconds;

                    httpClient.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
                    httpRequest.Headers.TryAddWithoutValidation("x-api-key", FirstStepValidateApiKey);
                    httpRequest.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

                    using (HttpResponseMessage response = await httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false))
                    {
                        string responseBody = response.Content == null
                            ? string.Empty
                            : await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                        if ((int)response.StatusCode != 200)
                        {
                            result.AddError(
                                "FirstStep API Communication",
                                "FirstStep " + operationLabel + " API (" + apiPath + ") communication issue. Expected HTTP 200 but received HTTP " +
                                (int)response.StatusCode + " " + response.ReasonPhrase +
                                BuildResponseBodyMessage(responseBody)
                            );
                            return result;
                        }

                        result.Merge(ParseFirstStepValidateApiResponse(
                            responseBody,
                            playerBuildSettingsSnapshot,
                            endpoint
                        ));
                    }
                }
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    result.AddError("FirstStep API", "FirstStep " + operationLabel + " API (" + apiPath + ") call was canceled.");
                }
                else
                {
                    result.AddError("FirstStep API", "FirstStep " + operationLabel + " API (" + apiPath + ") call timed out.");
                }
            }
            catch (Exception ex)
            {
                result.AddError("FirstStep API", "FirstStep " + operationLabel + " API (" + apiPath + ") call failed: " + ex.Message);
            }

            return result;
        }

        private static FirstStepValidateRequest CreateFirstStepValidateRequest(
            FacebookSnapshot facebookSnapshot,
            GameAnalyticsSnapshot gameAnalyticsSnapshot,
            PlayerBuildSettingsSnapshot playerBuildSettingsSnapshot,
            ValidationResult result)
        {
            if (result == null)
            {
                result = new ValidationResult();
            }

            if (!IsFacebookSnapshotAvailableForFirstStepRequest(facebookSnapshot))
            {
                result.AddError("FirstStep API", "Facebook settings are not available for API validation.");
                return null;
            }

            if (gameAnalyticsSnapshot == null || !gameAnalyticsSnapshot.HasAsset || !gameAnalyticsSnapshot.HasRequiredFields)
            {
                result.AddError("FirstStep API", "GameAnalytics settings are not available for API validation.");
                return null;
            }

            if (playerBuildSettingsSnapshot == null || !playerBuildSettingsSnapshot.HasBuildTarget || !playerBuildSettingsSnapshot.IsSupportedMobileBuildTarget)
            {
                result.AddError("FirstStep API", "Android or iOS player build settings are required for API validation.");
                return null;
            }

            RuntimePlatform runtimePlatform;
            if (!TryGetGameAnalyticsRuntimePlatform(playerBuildSettingsSnapshot.BuildTarget, out runtimePlatform))
            {
                result.AddError("FirstStep API", "Unsupported build target for API validation: " + playerBuildSettingsSnapshot.BuildTarget);
                return null;
            }

            GameAnalyticsPlatformSnapshot gaPlatformSnapshot;
            if (!gameAnalyticsSnapshot.Platforms.TryGetValue(runtimePlatform, out gaPlatformSnapshot) || gaPlatformSnapshot == null)
            {
                result.AddError("FirstStep API", "GameAnalytics platform settings are not available for API validation.");
                return null;
            }

            var request = new FirstStepValidateRequest
            {
                packageName = playerBuildSettingsSnapshot.PackageName,
                os = playerBuildSettingsSnapshot.Os,
                gaGameKey = gaPlatformSnapshot.GameKey,
                gaSecretKey = gaPlatformSnapshot.SecretKey,
                fbAppId = GetFacebookAppIdForFirstStepRequest(facebookSnapshot),
                fbClientToken = facebookSnapshot.ClientToken,
                gameVersion = playerBuildSettingsSnapshot.GameVersion,
                bundleVersionCode = playerBuildSettingsSnapshot.BundleVersionCode,
                sdkVersion = FirstStepSdkVersion
            };

            ValidateFirstStepRequestPayload(request, result);
            return result.IsValid ? request : null;
        }

        private static string GetFacebookAppIdForFirstStepRequest(FacebookSnapshot snapshot)
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.AppId))
            {
                return string.Empty;
            }

            string appId = snapshot.AppId.Trim();
            if (snapshot.UsesPlatformFile &&
                snapshot.BuildTarget == BuildTarget.Android &&
                IsAndroidFacebookApplicationId(appId))
            {
                return appId.Substring(2);
            }

            return appId;
        }

        private static void ValidateFirstStepRequestPayload(
            FirstStepValidateRequest request,
            ValidationResult result)
        {
            if (request == null || result == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(request.packageName))
            {
                result.AddError("FirstStep API", "Request packageName is empty.");
            }

            if (request.os != "android" && request.os != "ios")
            {
                result.AddError("FirstStep API", "Request os must be android or ios.");
            }

            if (string.IsNullOrWhiteSpace(request.gaGameKey))
            {
                result.AddError("FirstStep API", "Request gaGameKey is empty.");
            }

            if (string.IsNullOrWhiteSpace(request.gaSecretKey))
            {
                result.AddError("FirstStep API", "Request gaSecretKey is empty.");
            }

            if (string.IsNullOrWhiteSpace(request.fbAppId))
            {
                result.AddError("FirstStep API", "Request fbAppId is empty.");
            }

            if (string.IsNullOrWhiteSpace(request.fbClientToken))
            {
                result.AddError("FirstStep API", "Request fbClientToken is empty.");
            }

            if (string.IsNullOrWhiteSpace(request.gameVersion))
            {
                result.AddError("FirstStep API", "Request gameVersion is empty.");
            }

            if (string.IsNullOrWhiteSpace(request.bundleVersionCode))
            {
                result.AddError("FirstStep API", "Request bundleVersionCode is empty.");
            }

            if (string.IsNullOrWhiteSpace(request.sdkVersion))
            {
                result.AddError("FirstStep API", "Request sdkVersion is empty.");
            }
        }

        private static ValidationResult ParseFirstStepValidateApiResponse(
            string responseBody,
            PlayerBuildSettingsSnapshot playerBuildSettingsSnapshot,
            FirstStepApiEndpoint endpoint)
        {
            var result = new ValidationResult();

            if (string.IsNullOrWhiteSpace(responseBody))
            {
                result.AddError("FirstStep API", "FirstStep " + GetFirstStepApiOperationLabel(endpoint) + " API returned an empty response body.");
                return result;
            }

            FirstStepValidateResponse response;

            try
            {
                string normalizedJson = NormalizeFirstStepValidateResponseJson(responseBody);
                response = JsonUtility.FromJson<FirstStepValidateResponse>(normalizedJson);
            }
            catch (Exception ex)
            {
                result.AddError(
                    "FirstStep API",
                    "Failed to parse FirstStep " + GetFirstStepApiOperationLabel(endpoint) + " API response JSON: " + ex.Message +
                    BuildResponseBodyMessage(responseBody)
                );
                return result;
            }

            if (response == null)
            {
                result.AddError("FirstStep API", "Failed to parse FirstStep " + GetFirstStepApiOperationLabel(endpoint) + " API response JSON.");
                return result;
            }

            if (response.data == null)
            {
                result.AddError("FirstStep API", "FirstStep " + GetFirstStepApiOperationLabel(endpoint) + " API response data is empty.");
                return result;
            }

            FirstStepValidateResponseData data = response.data;
            string expectedSource = GetFirstStepExpectedResponseSource(endpoint);
            string apiPath = GetFirstStepApiPath(endpoint);
            string operationLabel = GetFirstStepApiOperationLabel(endpoint);

            result.AddInfo("FirstStep API", BuildFirstStepResponseSummary(response, data, playerBuildSettingsSnapshot, endpoint));

            if (!string.IsNullOrWhiteSpace(data.source) &&
                !string.Equals(data.source, expectedSource, StringComparison.OrdinalIgnoreCase))
            {
                result.AddWarning(
                    "FirstStep API",
                    "Unexpected response source '" + data.source.Trim() + "'. Expected '" + expectedSource + "' for " + apiPath + "."
                );
            }

            if (IsFirstStepPass(data.status))
            {
                result.AddInfo("FirstStep API", "FirstStep remote " + operationLabel + " passed. data.status=pass");
                return result;
            }

            if (IsFirstStepAttention(data.status))
            {
                AddFirstStepAttentionErrors(data, result, operationLabel);
                return result;
            }

            string status = string.IsNullOrWhiteSpace(data.status) ? "(empty)" : data.status;
            result.AddError(
                "FirstStep API",
                "Unknown FirstStep " + operationLabel + " data.status '" + status + "'. Expected 'pass' or 'attention'."
            );

            return result;
        }

        private static string NormalizeFirstStepValidateResponseJson(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return json;
            }

            string normalized = json;
            normalized = ReplaceJsonPropertyName(normalized, "ga.gameKey", "gaGameKey");
            normalized = ReplaceJsonPropertyName(normalized, "ga.secretKey", "gaSecretKey");
            normalized = ReplaceJsonPropertyName(normalized, "fb.appId", "fbAppId");
            normalized = ReplaceJsonPropertyName(normalized, "fb.clientToken", "fbClientToken");
            return normalized;
        }

        private static string ReplaceJsonPropertyName(string json, string sourcePropertyName, string targetPropertyName)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(sourcePropertyName) || string.IsNullOrEmpty(targetPropertyName))
            {
                return json;
            }

            string pattern = "\"" + Regex.Escape(sourcePropertyName) + "\"\\s*:";
            return Regex.Replace(json, pattern, "\"" + targetPropertyName + "\":");
        }

        private static string BuildFirstStepResponseSummary(
            FirstStepValidateResponse response,
            FirstStepValidateResponseData data,
            PlayerBuildSettingsSnapshot playerBuildSettingsSnapshot,
            FirstStepApiEndpoint endpoint)
        {
            var sb = new StringBuilder();
            string platformLabel = playerBuildSettingsSnapshot == null
                ? "Unknown"
                : playerBuildSettingsSnapshot.PlatformLabel;

            sb.Append("FirstStep remote ");
            sb.Append(GetFirstStepApiOperationLabel(endpoint));
            sb.Append(" result for ");
            sb.Append(platformLabel);

            if (data != null)
            {
                sb.Append(": data.status=");
                sb.Append(string.IsNullOrWhiteSpace(data.status) ? "(empty)" : data.status);

                AppendKeyValueIfNotEmpty(sb, "source", data.source);
                AppendKeyValueIfNotEmpty(sb, "checkedAt", data.checkedAt);

                string checksSummary = BuildFirstStepChecksSummary(data.checks);
                if (!string.IsNullOrEmpty(checksSummary))
                {
                    sb.Append("\nChecks: ");
                    sb.Append(checksSummary);
                }
            }
            else
            {
                sb.Append(": response data is empty");
            }

            if (response != null)
            {
                AppendKeyValueIfNotEmpty(sb, "response.status", response.status);
                AppendKeyValueIfNotEmpty(sb, "response.message", response.message);
            }

            return sb.ToString();
        }

        private static void AppendKeyValueIfNotEmpty(StringBuilder sb, string key, string value)
        {
            if (sb == null || string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            sb.Append(", ");
            sb.Append(key);
            sb.Append("=");
            sb.Append(value);
        }

        private static bool IsFirstStepPass(string status)
        {
            return string.Equals(status, FirstStepResultPass, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsFirstStepAttention(string status)
        {
            return string.Equals(status, FirstStepResultAttention, StringComparison.OrdinalIgnoreCase);
        }

        private static void AddFirstStepAttentionErrors(
            FirstStepValidateResponseData data,
            ValidationResult result,
            string operationLabel)
        {
            if (result == null)
            {
                return;
            }

            string failedChecks = data == null
                ? string.Empty
                : BuildFirstStepFailedChecksSummary(data.checks);

            if (!string.IsNullOrEmpty(failedChecks))
            {
                result.AddError(
                    "FirstStep API Checks",
                    "FirstStep remote " + operationLabel + " returned data.status=attention. Failed checks: " + failedChecks
                );
            }

            bool hasErrorDetail = false;

            if (data != null && data.errors != null)
            {
                for (int i = 0; i < data.errors.Length; i++)
                {
                    FirstStepValidateError error = data.errors[i];
                    if (error == null)
                    {
                        continue;
                    }

                    hasErrorDetail = true;
                    result.AddError("FirstStep API", BuildFirstStepErrorMessage(error));
                }
            }

            if (!hasErrorDetail && string.IsNullOrEmpty(failedChecks))
            {
                result.AddError(
                    "FirstStep API",
                    "FirstStep remote " + operationLabel + " returned data.status=attention, but no failed checks or detailed error messages were returned."
                );
            }
        }

        private static string BuildFirstStepChecksSummary(FirstStepValidateChecks checks)
        {
            if (checks == null)
            {
                return string.Empty;
            }

            var parts = new List<string>();
            AddFirstStepCheck(parts, "packageName", "Package Name / Bundle ID", checks.packageName);
            AddFirstStepCheck(parts, "ga.gameKey", "GA Game Key", checks.gaGameKey);
            AddFirstStepCheck(parts, "ga.secretKey", "GA Secret Key", checks.gaSecretKey);
            AddFirstStepCheck(parts, "fb.appId", "Facebook App ID", checks.fbAppId);
            AddFirstStepCheck(parts, "fb.clientToken", "Facebook Client Token", checks.fbClientToken);
            // keyHash is intentionally ignored because it is not an SDK setting validation target.

            return parts.Count == 0 ? string.Empty : string.Join(", ", parts.ToArray());
        }

        private static string BuildFirstStepFailedChecksSummary(FirstStepValidateChecks checks)
        {
            if (checks == null)
            {
                return string.Empty;
            }

            var parts = new List<string>();
            AddFirstStepFailedCheck(parts, "packageName", "Package Name / Bundle ID", checks.packageName);
            AddFirstStepFailedCheck(parts, "ga.gameKey", "GA Game Key", checks.gaGameKey);
            AddFirstStepFailedCheck(parts, "ga.secretKey", "GA Secret Key", checks.gaSecretKey);
            AddFirstStepFailedCheck(parts, "fb.appId", "Facebook App ID", checks.fbAppId);
            AddFirstStepFailedCheck(parts, "fb.clientToken", "Facebook Client Token", checks.fbClientToken);
            // keyHash is intentionally ignored.

            return parts.Count == 0 ? string.Empty : string.Join(", ", parts.ToArray());
        }

        private static void AddFirstStepCheck(List<string> parts, string field, string label, string status)
        {
            if (parts == null || string.IsNullOrWhiteSpace(field) || string.IsNullOrWhiteSpace(status))
            {
                return;
            }

            parts.Add(BuildFirstStepCheckMessage(field, label, status));
        }

        private static void AddFirstStepFailedCheck(List<string> parts, string field, string label, string status)
        {
            if (string.IsNullOrWhiteSpace(status) ||
                string.Equals(status.Trim(), "ok", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            AddFirstStepCheck(parts, field, label, status);
        }

        private static string BuildFirstStepCheckMessage(string field, string label, string status)
        {
            string trimmedStatus = string.IsNullOrWhiteSpace(status)
                ? "(empty)"
                : status.Trim();
            string meaning = GetFirstStepCheckStatusMeaning(trimmedStatus);
            string message = label + " (" + field + ")=" + trimmedStatus;

            return string.IsNullOrEmpty(meaning)
                ? message
                : message + " [" + meaning + "]";
        }

        private static string GetFirstStepCheckStatusMeaning(string status)
        {
            if (string.IsNullOrWhiteSpace(status))
            {
                return string.Empty;
            }

            switch (status.Trim().ToLowerInvariant())
            {
                case "ok":
                    return "passed";
                case "missing":
                    return "value is missing";
                case "invalid":
                    return "invalid format";
                case "mismatch":
                    return "PSL registered value mismatch";
                default:
                    return string.Empty;
            }
        }

        private static string BuildFirstStepErrorMessage(FirstStepValidateError error)
        {
            if (error == null)
            {
                return "FirstStep remote validation returned an empty error item.";
            }

            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(error.field))
            {
                parts.Add("field=" + error.field.Trim());
            }

            string codeMessage = BuildFirstStepErrorCodeMessage(error.code);
            if (!string.IsNullOrWhiteSpace(codeMessage))
            {
                parts.Add(codeMessage);
            }

            if (!string.IsNullOrWhiteSpace(error.message))
            {
                parts.Add(error.message.Trim());
            }

            return parts.Count == 0
                ? "FirstStep remote validation returned an error without details."
                : string.Join(" / ", parts.ToArray());
        }

        private static string BuildFirstStepErrorCodeMessage(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                return string.Empty;
            }

            string trimmedCode = code.Trim();
            string meaning = GetFirstStepErrorCodeMeaning(trimmedCode);

            return string.IsNullOrEmpty(meaning)
                ? "code=" + trimmedCode
                : "code=" + trimmedCode + " [" + meaning + "]";
        }

        private static string GetFirstStepErrorCodeMeaning(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                return string.Empty;
            }

            switch (code.Trim().ToUpperInvariant())
            {
                case "PACKAGE_INVALID":
                    return "package name / bundle ID format is invalid";
                case "PACKAGE_MISMATCH":
                    return "package name / bundle ID does not match PSL";
                case "GAME_NOT_FOUND":
                    return "PSL game was not found by packageName + os";
                case "GA_GAME_KEY_INVALID":
                    return "GA Game Key format is invalid";
                case "GA_GAME_KEY_MISMATCH":
                    return "GA Game Key does not match PSL";
                case "GA_SECRET_INVALID":
                    return "GA Secret Key format is invalid";
                case "GA_SECRET_MISMATCH":
                    return "GA Secret Key does not match PSL";
                case "FB_APP_ID_INVALID":
                    return "Facebook App ID format is invalid";
                case "FB_APP_ID_MISMATCH":
                    return "Facebook App ID does not match PSL";
                case "FB_CLIENT_TOKEN_MISSING":
                    return "Facebook Client Token is missing";
                case "FB_CLIENT_TOKEN_INVALID":
                    return "Facebook Client Token format is invalid";
                case "FB_CLIENT_TOKEN_MISMATCH":
                    return "Facebook Client Token does not match PSL";
                default:
                    return string.Empty;
            }
        }

        private static bool TryBuildFirstStepApiUri(FirstStepApiEndpoint endpoint, out Uri uri)
        {
            uri = null;

            string domain = FirstStepValidateApiDomain == null
                ? string.Empty
                : FirstStepValidateApiDomain.Trim();

            if (string.IsNullOrEmpty(domain))
            {
                return false;
            }

            if (domain.IndexOf("://", StringComparison.Ordinal) < 0)
            {
                domain = "https://" + domain;
            }

            string url = domain.TrimEnd('/') + GetFirstStepApiPath(endpoint);
            return Uri.TryCreate(url, UriKind.Absolute, out uri) &&
                   (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }

        private static string GetFirstStepApiPath(FirstStepApiEndpoint endpoint)
        {
            return endpoint == FirstStepApiEndpoint.BuildRegister
                ? FirstStepBuildRegisterApiPath
                : FirstStepValidateApiPath;
        }

        private static string GetFirstStepExpectedResponseSource(FirstStepApiEndpoint endpoint)
        {
            return endpoint == FirstStepApiEndpoint.BuildRegister
                ? FirstStepBuildRegisterResponseSource
                : FirstStepValidateResponseSource;
        }

        private static string GetFirstStepApiOperationLabel(FirstStepApiEndpoint endpoint)
        {
            return endpoint == FirstStepApiEndpoint.BuildRegister
                ? "build register"
                : "validation";
        }

        private static string BuildResponseBodyMessage(string responseBody)
        {
            if (string.IsNullOrWhiteSpace(responseBody))
            {
                return string.Empty;
            }

            string trimmed = responseBody.Trim();
            if (trimmed.Length > MaxApiResponseBodyLogLength)
            {
                trimmed = trimmed.Substring(0, MaxApiResponseBodyLogLength) + "...";
            }

            return "\nResponse: " + trimmed;
        }

        private static bool IsFacebookSnapshotAvailableForFirstStepRequest(FacebookSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return false;
            }

            if (snapshot.UsesPlatformFile)
            {
                return snapshot.HasPlatformFile &&
                       string.IsNullOrWhiteSpace(snapshot.PlatformFileReadError) &&
                       snapshot.AppIdEntryExists &&
                       snapshot.ClientTokenEntryExists &&
                       string.IsNullOrWhiteSpace(snapshot.AppIdResolutionError) &&
                       string.IsNullOrWhiteSpace(snapshot.ClientTokenResolutionError) &&
                       !string.IsNullOrWhiteSpace(snapshot.AppId) &&
                       !string.IsNullOrWhiteSpace(snapshot.ClientToken);
            }

            return snapshot.HasAsset &&
                   snapshot.HasRequiredFields &&
                   !string.IsNullOrWhiteSpace(snapshot.AppId) &&
                   !string.IsNullOrWhiteSpace(snapshot.ClientToken);
        }

        private static string BuildMissingFacebookPlatformFileMessage(FacebookSnapshot snapshot)
        {
            if (snapshot != null && snapshot.BuildTarget == BuildTarget.Android)
            {
                return "Android Facebook actual settings file was not found. Expected AndroidManifest at one of: " +
                       string.Join(", ", FacebookAndroidManifestCandidatePaths) +
                       ". Also searched under Assets/Plugins/Android for AndroidManifest*.xml.";
            }

            return "Facebook actual platform settings file was not found.";
        }

        private static string GetFacebookAppIdEntryLabel(BuildTarget buildTarget)
        {
            if (buildTarget == BuildTarget.Android)
            {
                return "AndroidManifest meta-data '" + AndroidFacebookApplicationIdMetadataName + "'";
            }

            return "Facebook App ID entry";
        }

        private static string GetFacebookClientTokenEntryLabel(BuildTarget buildTarget)
        {
            if (buildTarget == BuildTarget.Android)
            {
                return "AndroidManifest meta-data '" + AndroidFacebookClientTokenMetadataName + "'";
            }

            return "Facebook Client Token entry";
        }

        private static bool IsAndroidFacebookApplicationId(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            string trimmed = value.Trim();
            return trimmed.Length > 2 &&
                   trimmed.StartsWith("fb", StringComparison.OrdinalIgnoreCase) &&
                   IsDigitsOnly(trimmed.Substring(2));
        }

        private static string FindFacebookAndroidManifestPath()
        {
            string candidate = FindFirstExistingProjectFile(FacebookAndroidManifestCandidatePaths);
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }

            string androidPluginsRoot = ToAbsoluteProjectPath("Assets/Plugins/Android");
            if (!Directory.Exists(androidPluginsRoot))
            {
                return string.Empty;
            }

            string[] files = Directory.GetFiles(androidPluginsRoot, "AndroidManifest*", SearchOption.AllDirectories);
            string fallback = string.Empty;

            for (int i = 0; i < files.Length; i++)
            {
                string fileName = Path.GetFileName(files[i]);
                if (string.Equals(fileName, "AndroidManifest.xml", StringComparison.OrdinalIgnoreCase))
                {
                    return ToProjectRelativePath(files[i]);
                }

                if (string.IsNullOrEmpty(fallback) &&
                    fileName.StartsWith("AndroidManifest", StringComparison.OrdinalIgnoreCase))
                {
                    fallback = files[i];
                }
            }

            return string.IsNullOrEmpty(fallback) ? string.Empty : ToProjectRelativePath(fallback);
        }

        private static string FindFirstExistingProjectFile(string[] candidatePaths)
        {
            if (candidatePaths == null)
            {
                return string.Empty;
            }

            for (int i = 0; i < candidatePaths.Length; i++)
            {
                string candidate = candidatePaths[i];
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                if (File.Exists(ToAbsoluteProjectPath(candidate)))
                {
                    return NormalizePathForUnity(candidate);
                }
            }

            return string.Empty;
        }

        private static Dictionary<string, string> ReadAndroidManifestMetaData(string projectRelativePath)
        {
            string absolutePath = ToAbsoluteProjectPath(projectRelativePath);
            string content = File.ReadAllText(absolutePath);

            try
            {
                return ReadAndroidManifestMetaDataWithXml(content);
            }
            catch (Exception xmlException)
            {
                Dictionary<string, string> fallback = ReadAndroidManifestMetaDataWithRegex(content);
                if (fallback.Count > 0)
                {
                    return fallback;
                }

                throw new Exception("AndroidManifest XML could not be parsed: " + xmlException.Message);
            }
        }

        private static Dictionary<string, string> ReadAndroidManifestMetaDataWithXml(string content)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            var document = new XmlDocument();
            document.XmlResolver = null;
            document.LoadXml(content);

            XmlNodeList nodes = document.GetElementsByTagName("meta-data");
            for (int i = 0; i < nodes.Count; i++)
            {
                XmlElement element = nodes[i] as XmlElement;
                if (element == null)
                {
                    continue;
                }

                string name = GetAndroidXmlAttribute(element, "name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                values[name.Trim()] = GetAndroidXmlAttribute(element, "value");
            }

            return values;
        }

        private static Dictionary<string, string> ReadAndroidManifestMetaDataWithRegex(string content)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(content))
            {
                return values;
            }

            MatchCollection matches = Regex.Matches(content, "<\\s*meta-data\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            for (int i = 0; i < matches.Count; i++)
            {
                string tag = matches[i].Value;
                string name = GetXmlAttributeFromTag(tag, "android:name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = GetXmlAttributeFromTag(tag, "name");
                }

                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                string value = GetXmlAttributeFromTag(tag, "android:value");
                if (string.IsNullOrEmpty(value))
                {
                    value = GetXmlAttributeFromTag(tag, "value");
                }

                values[name.Trim()] = value;
            }

            return values;
        }

        private static string GetAndroidXmlAttribute(XmlElement element, string localName)
        {
            if (element == null || string.IsNullOrEmpty(localName))
            {
                return string.Empty;
            }

            string value = element.GetAttribute(localName, AndroidXmlNamespace);
            if (string.IsNullOrEmpty(value))
            {
                value = element.GetAttribute("android:" + localName);
            }

            if (string.IsNullOrEmpty(value))
            {
                value = element.GetAttribute(localName);
            }

            return value == null ? string.Empty : value.Trim();
        }

        private static string GetXmlAttributeFromTag(string tag, string attributeName)
        {
            if (string.IsNullOrEmpty(tag) || string.IsNullOrEmpty(attributeName))
            {
                return string.Empty;
            }

            string pattern = "\\b" + Regex.Escape(attributeName) + "\\s*=\\s*(['\\\"])(.*?)\\1";
            Match match = Regex.Match(tag, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
            return match.Success ? match.Groups[2].Value.Trim() : string.Empty;
        }

        private static Dictionary<string, string> ReadAndroidStringResources()
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);

            for (int rootIndex = 0; rootIndex < FacebookAndroidStringResourceRootPaths.Length; rootIndex++)
            {
                string androidRoot = ToAbsoluteProjectPath(FacebookAndroidStringResourceRootPaths[rootIndex]);
                if (!Directory.Exists(androidRoot))
                {
                    continue;
                }

                string[] files = Directory.GetFiles(androidRoot, "*.xml", SearchOption.AllDirectories);
                for (int i = 0; i < files.Length; i++)
                {
                    string normalizedPath = NormalizePathForUnity(files[i]);
                    if (normalizedPath.IndexOf("/res/values/", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    ReadAndroidStringResourceFile(files[i], values);
                }
            }

            return values;
        }

        private static void ReadAndroidStringResourceFile(string absolutePath, Dictionary<string, string> values)
        {
            if (string.IsNullOrWhiteSpace(absolutePath) || values == null)
            {
                return;
            }

            try
            {
                var document = new XmlDocument();
                document.XmlResolver = null;
                document.LoadXml(File.ReadAllText(absolutePath));

                XmlNodeList stringNodes = document.GetElementsByTagName("string");
                for (int i = 0; i < stringNodes.Count; i++)
                {
                    XmlElement element = stringNodes[i] as XmlElement;
                    if (element == null)
                    {
                        continue;
                    }

                    string name = element.GetAttribute("name");
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    values[name.Trim()] = element.InnerText == null ? string.Empty : element.InnerText.Trim();
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[First Stage Settings] Failed to read Android string resource file '" + ToProjectRelativePath(absolutePath) + "': " + ex.Message);
            }
        }

        private static string ResolveAndroidResourceReference(
            string value,
            Dictionary<string, string> stringResources,
            string fieldName,
            string sourcePath,
            out string error)
        {
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            string trimmed = value.Trim();

            if (trimmed.StartsWith("${", StringComparison.Ordinal) && trimmed.EndsWith("}", StringComparison.Ordinal))
            {
                error = "Unresolved Android manifest placeholder '" + trimmed + "' for " + fieldName + " in " + sourcePath + ".";
                return string.Empty;
            }

            string resourceName;
            if (TryGetAndroidStringResourceName(trimmed, out resourceName))
            {
                string resolved;
                if (stringResources != null && stringResources.TryGetValue(resourceName, out resolved))
                {
                    return string.IsNullOrWhiteSpace(resolved) ? string.Empty : resolved.Trim();
                }

                error = "Could not resolve Android string resource '" + trimmed + "' for " + fieldName +
                        " in " + sourcePath + ". Add the string under Assets/Plugins/Android/**/res/values/*.xml or Assets/FacebookSDK/Plugins/Android/**/res/values/*.xml.";
                return string.Empty;
            }

            if (trimmed.StartsWith("@", StringComparison.Ordinal))
            {
                error = "Unsupported Android resource reference '" + trimmed + "' for " + fieldName +
                        " in " + sourcePath + ". Only @string/... values are supported.";
                return string.Empty;
            }

            return trimmed;
        }

        private static bool TryGetAndroidStringResourceName(string value, out string resourceName)
        {
            resourceName = string.Empty;

            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            string trimmed = value.Trim();
            const string simplePrefix = "@string/";
            int simpleIndex = trimmed.IndexOf(simplePrefix, StringComparison.OrdinalIgnoreCase);
            if (simpleIndex == 0)
            {
                resourceName = trimmed.Substring(simplePrefix.Length).Trim();
                return !string.IsNullOrEmpty(resourceName);
            }

            const string packagePrefix = ":string/";
            int packageIndex = trimmed.IndexOf(packagePrefix, StringComparison.OrdinalIgnoreCase);
            if (trimmed.StartsWith("@", StringComparison.Ordinal) && packageIndex >= 0)
            {
                resourceName = trimmed.Substring(packageIndex + packagePrefix.Length).Trim();
                return !string.IsNullOrEmpty(resourceName);
            }

            return false;
        }

        private static string GetBuildTargetPlatformLabel(BuildTarget buildTarget)
        {
            switch (buildTarget)
            {
                case BuildTarget.Android:
                    return "Google(Android)";

                case BuildTarget.iOS:
                    return "iOS";

                default:
                    return buildTarget.ToString();
            }
        }

        private static string ToAbsoluteProjectPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            string normalizedPath = NormalizePathForUnity(path);
            if (Path.IsPathRooted(normalizedPath))
            {
                return normalizedPath;
            }

            return Path.GetFullPath(Path.Combine(GetProjectRootPath(), normalizedPath));
        }

        private static string ToProjectRelativePath(string absolutePath)
        {
            if (string.IsNullOrWhiteSpace(absolutePath))
            {
                return string.Empty;
            }

            string fullPath = NormalizePathForUnity(Path.GetFullPath(absolutePath));
            string projectRoot = NormalizePathForUnity(Path.GetFullPath(GetProjectRootPath())).TrimEnd('/') + "/";

            if (fullPath.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
            {
                return fullPath.Substring(projectRoot.Length);
            }

            return fullPath;
        }

        private static string GetProjectRootPath()
        {
            return Directory.GetCurrentDirectory();
        }

        private static string NormalizePathForUnity(string path)
        {
            return string.IsNullOrEmpty(path) ? string.Empty : path.Replace('\\', '/');
        }

        private static FacebookSnapshot ReadFacebookSnapshot(UnityObject settings, BuildTarget? buildTarget)
        {
            var snapshot = new FacebookSnapshot
            {
                Asset = settings,
                HasAsset = settings != null,
                AssetPath = GetAssetPathLabel(settings),
                PlatformLabel = buildTarget.HasValue ? GetBuildTargetPlatformLabel(buildTarget.Value) : string.Empty
            };

            if (buildTarget.HasValue)
            {
                snapshot.BuildTarget = buildTarget.Value;
            }

            ReadFacebookAssetValues(settings, snapshot);

            // Android has a pre-generated manifest through the Facebook SDK's
            // Regenerate Android Manifest flow, so validate and send the values
            // from the actual manifest file.
            //
            // iOS Info.plist is generated inside the Xcode project during the
            // Unity iOS build, so editor/pre-build validation must use the
            // FacebookSettings.asset values instead.
            if (buildTarget.HasValue && buildTarget.Value == BuildTarget.Android)
            {
                snapshot.UsesPlatformFile = true;
                ReadFacebookAndroidManifestSnapshot(snapshot);
            }

            return snapshot;
        }

        private static void ReadFacebookAssetValues(UnityObject settings, FacebookSnapshot snapshot)
        {
            if (settings == null || snapshot == null)
            {
                return;
            }

            var so = new SerializedObject(settings);
            so.Update();

            SerializedProperty appLabels = so.FindProperty("appLabels");
            SerializedProperty appIds = so.FindProperty("appIds");
            SerializedProperty clientTokens = so.FindProperty("clientTokens");
            SerializedProperty selectedAppIndex = so.FindProperty("selectedAppIndex");

            snapshot.HasRequiredFields = appLabels != null && appIds != null && clientTokens != null;

            if (!snapshot.HasRequiredFields)
            {
                return;
            }

            snapshot.AppCount = Mathf.Max(appLabels.arraySize, Mathf.Max(appIds.arraySize, clientTokens.arraySize));
            snapshot.SelectedAppIndex = selectedAppIndex != null
                ? Mathf.Clamp(selectedAppIndex.intValue, 0, Mathf.Max(0, snapshot.AppCount - 1))
                : 0;

            snapshot.AppName = GetStringArrayValue(appLabels, snapshot.SelectedAppIndex);
            snapshot.AppId = GetStringArrayValue(appIds, snapshot.SelectedAppIndex);
            snapshot.ClientToken = GetStringArrayValue(clientTokens, snapshot.SelectedAppIndex);
        }

        private static void ReadFacebookAndroidManifestSnapshot(FacebookSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return;
            }

            snapshot.PlatformFilePath = FindFacebookAndroidManifestPath();
            snapshot.HasPlatformFile = !string.IsNullOrWhiteSpace(snapshot.PlatformFilePath);

            if (!snapshot.HasPlatformFile)
            {
                snapshot.AppId = string.Empty;
                snapshot.ClientToken = string.Empty;
                return;
            }

            try
            {
                Dictionary<string, string> manifestMetadata = ReadAndroidManifestMetaData(snapshot.PlatformFilePath);
                Dictionary<string, string> stringResources = ReadAndroidStringResources();

                string appIdRaw;
                snapshot.AppIdEntryExists = manifestMetadata.TryGetValue(AndroidFacebookApplicationIdMetadataName, out appIdRaw);
                snapshot.AppIdRawValue = appIdRaw;

                string clientTokenRaw;
                snapshot.ClientTokenEntryExists = manifestMetadata.TryGetValue(AndroidFacebookClientTokenMetadataName, out clientTokenRaw);
                snapshot.ClientTokenRawValue = clientTokenRaw;

                if (snapshot.AppIdEntryExists)
                {
                    snapshot.AppId = ResolveAndroidResourceReference(
                        appIdRaw,
                        stringResources,
                        AndroidFacebookApplicationIdMetadataName,
                        snapshot.PlatformFilePath,
                        out snapshot.AppIdResolutionError
                    );
                }
                else
                {
                    snapshot.AppId = string.Empty;
                }

                if (snapshot.ClientTokenEntryExists)
                {
                    snapshot.ClientToken = ResolveAndroidResourceReference(
                        clientTokenRaw,
                        stringResources,
                        AndroidFacebookClientTokenMetadataName,
                        snapshot.PlatformFilePath,
                        out snapshot.ClientTokenResolutionError
                    );
                }
                else
                {
                    snapshot.ClientToken = string.Empty;
                }
            }
            catch (Exception ex)
            {
                snapshot.PlatformFileReadError = ex.Message;
                snapshot.AppId = string.Empty;
                snapshot.ClientToken = string.Empty;
            }
        }

        private static GameAnalyticsSnapshot ReadGameAnalyticsSnapshot(UnityObject settings)
        {
            var snapshot = new GameAnalyticsSnapshot
            {
                Asset = settings,
                HasAsset = settings != null,
                AssetPath = GetAssetPathLabel(settings)
            };

            if (settings == null)
            {
                return snapshot;
            }

            var so = new SerializedObject(settings);
            so.Update();

            SerializedProperty gameKeys = so.FindProperty("gameKey");
            SerializedProperty secretKeys = so.FindProperty("secretKey");
            SerializedProperty platforms = so.FindProperty("Platforms");

            snapshot.HasRequiredFields = gameKeys != null && secretKeys != null && platforms != null;

            if (!snapshot.HasRequiredFields)
            {
                return snapshot;
            }

            snapshot.PlatformCount = platforms.arraySize;
            AddGameAnalyticsPlatformSnapshot(snapshot, platforms, gameKeys, secretKeys, RuntimePlatform.Android);
            AddGameAnalyticsPlatformSnapshot(snapshot, platforms, gameKeys, secretKeys, RuntimePlatform.IPhonePlayer);

            return snapshot;
        }

        private static PlayerBuildSettingsSnapshot ReadPlayerBuildSettingsSnapshot(BuildTarget? buildTarget)
        {
            var snapshot = new PlayerBuildSettingsSnapshot
            {
                HasBuildTarget = buildTarget.HasValue,
                BuildTarget = buildTarget.HasValue ? buildTarget.Value : default(BuildTarget),
                BuildTargetGroup = BuildTargetGroup.Unknown,
                PlatformLabel = buildTarget.HasValue ? buildTarget.Value.ToString() : "Unknown",
                Os = string.Empty,
                PackageName = string.Empty,
                GameVersion = PlayerSettings.bundleVersion,
                BundleVersionCode = string.Empty
            };

            if (!buildTarget.HasValue)
            {
                return snapshot;
            }

            switch (buildTarget.Value)
            {
                case BuildTarget.Android:
                    snapshot.IsSupportedMobileBuildTarget = true;
                    snapshot.BuildTargetGroup = BuildTargetGroup.Android;
                    snapshot.PlatformLabel = "Google(Android)";
                    snapshot.Os = "android";
                    snapshot.PackageName = PlayerSettings.GetApplicationIdentifier(BuildTargetGroup.Android);
                    snapshot.BundleVersionCode = PlayerSettings.Android.bundleVersionCode.ToString();
                    break;

                case BuildTarget.iOS:
                    snapshot.IsSupportedMobileBuildTarget = true;
                    snapshot.BuildTargetGroup = BuildTargetGroup.iOS;
                    snapshot.PlatformLabel = "iOS";
                    snapshot.Os = "ios";
                    snapshot.PackageName = PlayerSettings.GetApplicationIdentifier(BuildTargetGroup.iOS);
                    snapshot.BundleVersionCode = PlayerSettings.iOS.buildNumber;
                    break;
            }

            return snapshot;
        }

        private static void AddGameAnalyticsPlatformSnapshot(
            GameAnalyticsSnapshot snapshot,
            SerializedProperty platforms,
            SerializedProperty gameKeys,
            SerializedProperty secretKeys,
            RuntimePlatform platform)
        {
            int index = FindRuntimePlatformIndex(platforms, platform);

            var platformSnapshot = new GameAnalyticsPlatformSnapshot
            {
                Exists = index >= 0,
                Index = index,
                GameKeyEntryExists = index >= 0 && HasStringArrayElement(gameKeys, index),
                SecretKeyEntryExists = index >= 0 && HasStringArrayElement(secretKeys, index),
                GameKey = index >= 0 ? GetStringArrayValue(gameKeys, index) : string.Empty,
                SecretKey = index >= 0 ? GetStringArrayValue(secretKeys, index) : string.Empty
            };

            snapshot.Platforms[platform] = platformSnapshot;
        }

        public static int FindRuntimePlatformIndex(SerializedProperty platforms, RuntimePlatform platform)
        {
            if (platforms == null || !platforms.isArray)
            {
                return -1;
            }

            string targetName = platform.ToString();
            int targetValue = (int)platform;

            for (int i = 0; i < platforms.arraySize; i++)
            {
                SerializedProperty element = platforms.GetArrayElementAtIndex(i);

                if (element.propertyType == SerializedPropertyType.Enum)
                {
                    int enumIndex = element.enumValueIndex;

                    if (enumIndex >= 0 && enumIndex < element.enumNames.Length && element.enumNames[enumIndex] == targetName)
                    {
                        return i;
                    }

                    if (enumIndex >= 0 && enumIndex < element.enumDisplayNames.Length && element.enumDisplayNames[enumIndex] == targetName)
                    {
                        return i;
                    }
                }
                else if (element.propertyType == SerializedPropertyType.Integer && element.intValue == targetValue)
                {
                    return i;
                }
            }

            return -1;
        }

        public static bool IsFirstStepMobileBuildTarget(BuildTarget buildTarget)
        {
            return buildTarget == BuildTarget.Android || buildTarget == BuildTarget.iOS;
        }

        private static bool TryGetGameAnalyticsRuntimePlatform(BuildTarget buildTarget, out RuntimePlatform runtimePlatform)
        {
            switch (buildTarget)
            {
                case BuildTarget.Android:
                    runtimePlatform = RuntimePlatform.Android;
                    return true;

                case BuildTarget.iOS:
                    runtimePlatform = RuntimePlatform.IPhonePlayer;
                    return true;

                default:
                    runtimePlatform = default(RuntimePlatform);
                    return false;
            }
        }

        private static RuntimePlatform[] GetRequiredGameAnalyticsPlatforms(
            GameAnalyticsPlatformValidationMode mode,
            BuildTarget? buildTarget)
        {
            if (mode == GameAnalyticsPlatformValidationMode.AllMobilePlatforms)
            {
                return new[]
                {
                    RuntimePlatform.Android,
                    RuntimePlatform.IPhonePlayer
                };
            }

            if (!buildTarget.HasValue)
            {
                return Array.Empty<RuntimePlatform>();
            }

            switch (buildTarget.Value)
            {
                case BuildTarget.Android:
                    return new[] { RuntimePlatform.Android };

                case BuildTarget.iOS:
                    return new[] { RuntimePlatform.IPhonePlayer };

                default:
                    return Array.Empty<RuntimePlatform>();
            }
        }

        private static string GetGameAnalyticsPlatformLabel(RuntimePlatform platform)
        {
            switch (platform)
            {
                case RuntimePlatform.Android:
                    return "Android";

                case RuntimePlatform.IPhonePlayer:
                    return "iOS";

                default:
                    return platform.ToString();
            }
        }

        private static string GetGameAnalyticsPlatformSource(RuntimePlatform platform)
        {
            return "GameAnalytics " + GetGameAnalyticsPlatformLabel(platform);
        }

        private static bool HasStringArrayElement(SerializedProperty array, int index)
        {
            if (array == null || !array.isArray || index < 0 || index >= array.arraySize)
            {
                return false;
            }

            SerializedProperty element = array.GetArrayElementAtIndex(index);
            return element != null && element.propertyType == SerializedPropertyType.String;
        }

        private static string GetStringArrayValue(SerializedProperty array, int index)
        {
            if (array == null || !array.isArray || index < 0 || index >= array.arraySize)
            {
                return string.Empty;
            }

            SerializedProperty element = array.GetArrayElementAtIndex(index);
            return element != null && element.propertyType == SerializedPropertyType.String
                ? element.stringValue
                : string.Empty;
        }

        private static bool IsDigitsOnly(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            for (int i = 0; i < value.Length; i++)
            {
                if (!char.IsDigit(value[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static string GetAssetPathLabel(UnityObject asset)
        {
            if (asset == null)
            {
                return string.Empty;
            }

            string path = AssetDatabase.GetAssetPath(asset);
            return string.IsNullOrEmpty(path) ? "(unsaved object)" : path;
        }

        private static bool ExecuteFirstAvailableMenuItem(string[] menuPaths)
        {
            if (menuPaths == null || menuPaths.Length == 0)
            {
                return false;
            }

            for (int i = 0; i < menuPaths.Length; i++)
            {
                string menuPath = menuPaths[i];
                if (string.IsNullOrEmpty(menuPath))
                {
                    continue;
                }

                try
                {
                    if (EditorApplication.ExecuteMenuItem(menuPath))
                    {
                        SaveAndRefreshAssets();
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[First Stage Settings] Failed to execute menu item '" + menuPath + "': " + ex.Message);
                }
            }

            return false;
        }

        private static string JoinMenuPathLabels(string[] menuPaths)
        {
            if (menuPaths == null || menuPaths.Length == 0)
            {
                return string.Empty;
            }

            return string.Join(", ", menuPaths);
        }

        private static UnityObject FindAssetByPathOrType(
            string defaultPath,
            string assetName,
            string expectedFullTypeName)
        {
            UnityObject asset = AssetDatabase.LoadAssetAtPath<UnityObject>(defaultPath);
            if (IsExpectedAsset(asset, expectedFullTypeName))
            {
                return asset;
            }

            string[] guids = AssetDatabase.FindAssets($"{assetName} t:ScriptableObject");

            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                asset = AssetDatabase.LoadAssetAtPath<UnityObject>(path);

                if (IsExpectedAsset(asset, expectedFullTypeName))
                {
                    return asset;
                }
            }

            return null;
        }

        private static bool IsExpectedAsset(UnityObject asset, string expectedFullTypeName)
        {
            if (asset == null)
            {
                return false;
            }

            Type type = asset.GetType();
            return type.FullName == expectedFullTypeName || type.Name == expectedFullTypeName;
        }
    }
}
