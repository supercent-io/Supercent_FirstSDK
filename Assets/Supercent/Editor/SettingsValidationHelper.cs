using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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

        private const string FacebookSettingsTypeName = "Facebook.Unity.Settings.FacebookSettings";
        private const string GameAnalyticsSettingsTypeName = "GameAnalyticsSDK.Setup.Settings";

        private static readonly string[] FacebookSettingsMenuPaths =
        {
            "Facebook/Edit Settings"
        };

        private static readonly string[] GameAnalyticsSettingsMenuPaths =
        {
            "Window/GameAnalytics/Select Settings"
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

            public GameAnalyticsPlatformValidationMode GameAnalyticsMode = GameAnalyticsPlatformValidationMode.AllMobilePlatforms;
            public BuildTarget? BuildTarget;
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

            FacebookSnapshot facebookSnapshot = options.ValidateFacebook
                ? ReadFacebookSnapshot(facebookAsset)
                : null;

            GameAnalyticsSnapshot gameAnalyticsSnapshot = options.ValidateGameAnalytics
                ? ReadGameAnalyticsSnapshot(gameAnalyticsAsset)
                : null;

            var result = new ValidationResult();

            if (facebookSnapshot != null)
            {
                result.Merge(await ValidateFacebookSnapshotAsync(facebookSnapshot, cancellationToken).ConfigureAwait(false));
            }

            if (gameAnalyticsSnapshot != null)
            {
                result.Merge(await ValidateGameAnalyticsSnapshotAsync(
                    gameAnalyticsSnapshot,
                    options.GameAnalyticsMode,
                    options.BuildTarget,
                    cancellationToken
                ).ConfigureAwait(false));
            }

            return result;
        }

        public static async Task<ValidationResult> ValidateFacebookAsync(
            UnityObject facebookSettings,
            CancellationToken cancellationToken = default)
        {
            FacebookSnapshot snapshot = ReadFacebookSnapshot(facebookSettings);
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

            if (result.IsValid)
            {
                Debug.Log("[First Stage Settings] " + message);
                EditorUtility.DisplayDialog(title, message, "OK");
            }
            else
            {
                Debug.LogError("[First Stage Settings] " + message);
                EditorUtility.DisplayDialog(title, message, "OK");
            }
        }

        private static async Task<ValidationResult> ValidateFacebookSnapshotAsync(
            FacebookSnapshot snapshot,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

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

            // Future API validation entry point.
            // Example: call Meta/Facebook Graph API with App ID + Client Token here.
            // Keep ConfigureAwait(false) in future awaits to avoid deadlocks when build validation waits synchronously.
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);

            if (result.IsValid)
            {
                result.AddInfo("Facebook", "Facebook settings look valid.");
            }

            return result;
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

        private static FacebookSnapshot ReadFacebookSnapshot(UnityObject settings)
        {
            var snapshot = new FacebookSnapshot
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

            SerializedProperty appLabels = so.FindProperty("appLabels");
            SerializedProperty appIds = so.FindProperty("appIds");
            SerializedProperty clientTokens = so.FindProperty("clientTokens");
            SerializedProperty selectedAppIndex = so.FindProperty("selectedAppIndex");

            snapshot.HasRequiredFields = appLabels != null && appIds != null && clientTokens != null;

            if (!snapshot.HasRequiredFields)
            {
                return snapshot;
            }

            snapshot.AppCount = Mathf.Max(appLabels.arraySize, Mathf.Max(appIds.arraySize, clientTokens.arraySize));
            snapshot.SelectedAppIndex = selectedAppIndex != null
                ? Mathf.Clamp(selectedAppIndex.intValue, 0, Mathf.Max(0, snapshot.AppCount - 1))
                : 0;

            snapshot.AppName = GetStringArrayValue(appLabels, snapshot.SelectedAppIndex);
            snapshot.AppId = GetStringArrayValue(appIds, snapshot.SelectedAppIndex);
            snapshot.ClientToken = GetStringArrayValue(clientTokens, snapshot.SelectedAppIndex);

            return snapshot;
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
