using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityObject = UnityEngine.Object;

// UPDATED 2026-06-23: Values are edited as local drafts and are only written to assets when Save is clicked.
namespace Supercent.Edit
{
    public class FirstStageSettings : EditorWindow
    {
        private const string FacebookDraftPrefix = "facebook.";
        private const string GameAnalyticsDraftPrefix = "gameanalytics.";

        private static GUIStyle sectionTitleStyle;
        private static GUIStyle platformTitleStyle;
        private static GUIStyle dirtyFieldLabelStyle;

        private readonly Dictionary<string, string> draftStringValues = new Dictionary<string, string>();

        private UnityObject facebookSettings;
        private UnityObject gameAnalyticsSettings;

        private Vector2 scroll;
        private int facebookSelectedAppIndex = -1;

        private static GUIStyle SectionTitleStyle
        {
            get
            {
                if (sectionTitleStyle == null)
                {
                    sectionTitleStyle = new GUIStyle(EditorStyles.boldLabel)
                    {
                        fontSize = 18,
                        fontStyle = FontStyle.Bold,
                        alignment = TextAnchor.MiddleLeft,
                        margin = new RectOffset(0, 0, 0, 0),
                        padding = new RectOffset(0, 0, 0, 0)
                    };
                }

                return sectionTitleStyle;
            }
        }

        private static GUIStyle PlatformTitleStyle
        {
            get
            {
                if (platformTitleStyle == null)
                {
                    platformTitleStyle = new GUIStyle(EditorStyles.boldLabel)
                    {
                        fontSize = 13,
                        fontStyle = FontStyle.Bold,
                        alignment = TextAnchor.MiddleLeft,
                        margin = new RectOffset(0, 0, 0, 0),
                        padding = new RectOffset(0, 0, 0, 0)
                    };
                }

                return platformTitleStyle;
            }
        }

        private static GUIStyle DirtyFieldLabelStyle
        {
            get
            {
                if (dirtyFieldLabelStyle == null)
                {
                    Color dirtyColor = EditorGUIUtility.isProSkin
                        ? new Color(1.0f, 0.78f, 0.32f, 1.0f)
                        : new Color(0.75f, 0.35f, 0.0f, 1.0f);

                    dirtyFieldLabelStyle = new GUIStyle(EditorStyles.boldLabel)
                    {
                        fontStyle = FontStyle.Bold,
                        alignment = TextAnchor.MiddleLeft,
                        margin = new RectOffset(0, 0, 0, 0),
                        padding = new RectOffset(0, 0, 0, 0)
                    };

                    dirtyFieldLabelStyle.normal.textColor = dirtyColor;
                    dirtyFieldLabelStyle.focused.textColor = dirtyColor;
                    dirtyFieldLabelStyle.hover.textColor = dirtyColor;
                    dirtyFieldLabelStyle.active.textColor = dirtyColor;
                }

                return dirtyFieldLabelStyle;
            }
        }

        [MenuItem("Supercent/First Stage Settings")]
        static void Open() => GetWindow<FirstStageSettings>("First Stage Settings");

        void OnEnable()
        {
            ClearDrafts();
            ReloadAssets();
        }

        void OnGUI()
        {
            EditorGUILayout.Space(6);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Reload Settings Assets", GUILayout.Height(26)))
                {
                    ClearDrafts();
                    ReloadAssets();
                    GUI.FocusControl(null);
                    Repaint();
                }

                GUILayout.FlexibleSpace();
            }

            EditorGUILayout.Space(6);

            scroll = EditorGUILayout.BeginScrollView(scroll);

            DrawFacebookSettings();
            EditorGUILayout.Space(12);
            DrawGameAnalyticsSettings();

            EditorGUILayout.EndScrollView();
        }

        void ReloadAssets()
        {
            facebookSettings = SettingsValidationHelper.FindFacebookSettingsAsset();
            gameAnalyticsSettings = SettingsValidationHelper.FindGameAnalyticsSettingsAsset();
            facebookSelectedAppIndex = -1;
        }

        private void DrawFacebookSettings()
        {
            DrawSectionTitle("Facebook Settings");

            using (new EditorGUILayout.VerticalScope("box"))
            {
                if (DrawAssetRow("FacebookSettings Asset", ref facebookSettings))
                {
                    ClearDraftsWithPrefix(FacebookDraftPrefix);
                    facebookSelectedAppIndex = -1;
                }

                if (facebookSettings == null)
                {
                    EditorGUILayout.HelpBox(
                        "FacebookSettings.asset was not found. Click Find Facebook Settings File to open the Facebook SDK settings menu, or assign the asset manually.",
                        MessageType.Warning
                    );

                    if (GUILayout.Button("Find Facebook Settings File", GUILayout.Height(26)))
                    {
                        OpenFacebookSettingsMenuOrShowMissingModuleDialog();
                    }

                    return;
                }

                var so = new SerializedObject(facebookSettings);
                so.Update();

                SerializedProperty appLabels = so.FindProperty("appLabels");
                SerializedProperty appIds = so.FindProperty("appIds");
                SerializedProperty clientTokens = so.FindProperty("clientTokens");
                SerializedProperty selectedAppIndex = so.FindProperty("selectedAppIndex");

                if (appLabels == null || appIds == null || clientTokens == null)
                {
                    EditorGUILayout.HelpBox(
                        "Required FacebookSettings fields were not found. The SDK may have changed its serialized field names.",
                        MessageType.Error
                    );
                    return;
                }

                int count = Mathf.Max(
                    1,
                    Mathf.Max(appLabels.arraySize, Mathf.Max(appIds.arraySize, clientTokens.arraySize))
                );

                if (facebookSelectedAppIndex < 0)
                {
                    facebookSelectedAppIndex = selectedAppIndex != null
                        ? Mathf.Clamp(selectedAppIndex.intValue, 0, count - 1)
                        : 0;
                }
                else
                {
                    facebookSelectedAppIndex = Mathf.Clamp(facebookSelectedAppIndex, 0, count - 1);
                }

                if (count > 1)
                {
                    EditorGUI.BeginChangeCheck();
                    facebookSelectedAppIndex = EditorGUILayout.Popup(
                        "Selected App",
                        facebookSelectedAppIndex,
                        BuildFacebookAppPopupLabels(appLabels, appIds)
                    );

                    if (EditorGUI.EndChangeCheck())
                    {
                        GUI.FocusControl(null);
                    }
                }

                EditorGUILayout.Space(4);

                int index = facebookSelectedAppIndex;

                string appNameOriginal = GetStringArrayValue(appLabels, index);
                string appIdOriginal = GetStringArrayValue(appIds, index);
                string clientTokenOriginal = GetStringArrayValue(clientTokens, index);

                DrawDraftTextField("App Name", GetFacebookDraftKey(index, "appName"), appNameOriginal);
                DrawDraftTextField("Facebook App ID", GetFacebookDraftKey(index, "appId"), appIdOriginal);
                DrawDraftTextField("Client Token", GetFacebookDraftKey(index, "clientToken"), clientTokenOriginal);

                DrawUnsavedChangesHelpBox(FacebookDraftPrefix);

                EditorGUILayout.Space(6);

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Validate Facebook Settings", GUILayout.Height(26)))
                    {
                        ValidateFacebookSettings(facebookSettings);
                    }

                    if (GUILayout.Button("Save Facebook Settings", GUILayout.Height(26)))
                    {
                        SaveFacebookSettings();
                    }
                }
            }
        }

        void DrawGameAnalyticsSettings()
        {
            DrawSectionTitle("GameAnalytics Settings");

            using (new EditorGUILayout.VerticalScope("box"))
            {
                if (DrawAssetRow("GameAnalytics Settings Asset", ref gameAnalyticsSettings))
                {
                    ClearDraftsWithPrefix(GameAnalyticsDraftPrefix);
                }

                if (gameAnalyticsSettings == null)
                {
                    EditorGUILayout.HelpBox(
                        "GameAnalytics Settings.asset was not found. Click Find GameAnalytics Settings File to open the GameAnalytics settings menu, or assign the asset manually.",
                        MessageType.Warning
                    );

                    if (GUILayout.Button("Find GameAnalytics Settings File", GUILayout.Height(26)))
                    {
                        OpenGameAnalyticsSettingsMenuOrShowMissingModuleDialog();
                    }

                    return;
                }

                var so = new SerializedObject(gameAnalyticsSettings);
                so.Update();

                SerializedProperty gameKeys = so.FindProperty("gameKey");
                SerializedProperty secretKeys = so.FindProperty("secretKey");
                SerializedProperty platforms = so.FindProperty("Platforms");

                if (gameKeys == null || secretKeys == null || platforms == null)
                {
                    EditorGUILayout.HelpBox(
                        "Required GameAnalytics Settings fields were not found. The SDK may have changed the gameKey / secretKey / Platforms field names.",
                        MessageType.Error
                    );
                    return;
                }

                DrawGameAnalyticsPlatformKeys(
                    so,
                    gameKeys,
                    secretKeys,
                    platforms,
                    RuntimePlatform.Android,
                    "Android"
                );

                EditorGUILayout.Space(8);

                DrawGameAnalyticsPlatformKeys(
                    so,
                    gameKeys,
                    secretKeys,
                    platforms,
                    RuntimePlatform.IPhonePlayer,
                    "iOS"
                );

                DrawUnsavedChangesHelpBox(GameAnalyticsDraftPrefix);

                EditorGUILayout.Space(6);

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Validate GameAnalytics Settings", GUILayout.Height(26)))
                    {
                        ValidateGameAnalyticsSettings(gameAnalyticsSettings);
                    }

                    if (GUILayout.Button("Save GameAnalytics Settings", GUILayout.Height(26)))
                    {
                        SaveGameAnalyticsSettings();
                    }
                }
            }
        }

        private static void DrawSectionTitle(string text)
        {
            const float titleHeight = 36f;
            const float lineHeight = 1f;

            EditorGUILayout.Space(10);

            Rect titleRect = EditorGUILayout.GetControlRect(false, titleHeight);
            titleRect.y += 2f;
            titleRect.height -= 4f;
            EditorGUI.LabelField(titleRect, text, SectionTitleStyle);

            // Keep the divider separated from the large title text.
            // Without this spacing, IMGUI can place the divider too close to the label
            // when custom font sizes are used.
            EditorGUILayout.Space(4);

            Rect lineRect = EditorGUILayout.GetControlRect(false, lineHeight);
            lineRect.x += 1f;
            lineRect.width -= 2f;
            EditorGUI.DrawRect(lineRect, new Color(0.35f, 0.35f, 0.35f, 1f));

            EditorGUILayout.Space(8);
        }

        private static void DrawPlatformTitle(string text)
        {
            const float titleHeight = 24f;

            EditorGUILayout.Space(8);

            Rect titleRect = EditorGUILayout.GetControlRect(false, titleHeight);
            titleRect.y += 1f;
            titleRect.height -= 2f;
            EditorGUI.LabelField(titleRect, text, PlatformTitleStyle);

            EditorGUILayout.Space(3);
        }

        private void DrawGameAnalyticsPlatformKeys(
            SerializedObject so,
            SerializedProperty gameKeys,
            SerializedProperty secretKeys,
            SerializedProperty platforms,
            RuntimePlatform platform,
            string label)
        {
            DrawPlatformTitle(label);

            int index = FindRuntimePlatformIndex(platforms, platform);

            if (index < 0)
            {
                EditorGUILayout.HelpBox(
                    $"The {label} platform entry does not exist in GameAnalytics Settings.",
                    MessageType.Info
                );

                if (GUILayout.Button($"Add {label} Platform Entry", GUILayout.Height(24)))
                {
                    so.ApplyModifiedProperties();

                    if (!InvokeGameAnalyticsAddPlatform(so.targetObject, platform))
                    {
                        AddGameAnalyticsPlatformFallback(so.targetObject, platform);
                    }

                    SaveAsset(so.targetObject);
                    GUIUtility.ExitGUI();
                }

                return;
            }

            string gameKeyOriginal = GetStringArrayValue(gameKeys, index);
            string secretKeyOriginal = GetStringArrayValue(secretKeys, index);

            DrawDraftTextField($"{label} Game Key", GetGameAnalyticsDraftKey(platform, "gameKey"), gameKeyOriginal);
            DrawDraftTextField($"{label} Secret Key", GetGameAnalyticsDraftKey(platform, "secretKey"), secretKeyOriginal);
        }

        private bool DrawAssetRow(string label, ref UnityObject asset)
        {
            EditorGUI.BeginChangeCheck();
            asset = EditorGUILayout.ObjectField(label, asset, typeof(ScriptableObject), false);
            bool changed = EditorGUI.EndChangeCheck();

            if (asset != null)
            {
                string path = AssetDatabase.GetAssetPath(asset);
                string displayPath = string.IsNullOrEmpty(path) ? "(unsaved object)" : path;

                // Read-only display only. Do not use SelectableLabel or TextField here.
                EditorGUILayout.LabelField("Asset Path", displayPath);
            }

            return changed;
        }

        private string DrawDraftTextField(string label, string draftKey, string originalValue)
        {
            originalValue = originalValue ?? string.Empty;

            string currentValue = GetDraftValue(draftKey, originalValue);
            bool isDirty = IsDraftDirty(draftKey, originalValue);

            using (new EditorGUILayout.HorizontalScope())
            {
                float labelWidth = Mathf.Max(80f, EditorGUIUtility.labelWidth - 4f);
                EditorGUILayout.LabelField(
                    label,
                    isDirty ? DirtyFieldLabelStyle : EditorStyles.label,
                    GUILayout.Width(labelWidth)
                );

                EditorGUI.BeginChangeCheck();
                string newValue = EditorGUILayout.TextField(currentValue);
                if (EditorGUI.EndChangeCheck())
                {
                    SetDraftValue(draftKey, originalValue, newValue);
                }
            }

            return GetDraftValue(draftKey, originalValue);
        }

        private void DrawUnsavedChangesHelpBox(string prefix)
        {
            if (!HasDraftsWithPrefix(prefix))
            {
                return;
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(
                "Unsaved changes are being held locally in this window. Click Save to write them to the settings asset, or Reload Settings Assets to discard them.",
                MessageType.Info
            );
        }

        private string GetDraftValue(string key, string originalValue)
        {
            string draftValue;
            return draftStringValues.TryGetValue(key, out draftValue)
                ? draftValue
                : (originalValue ?? string.Empty);
        }

        private bool IsDraftDirty(string key, string originalValue)
        {
            string draftValue;
            return draftStringValues.TryGetValue(key, out draftValue) &&
                   !string.Equals(draftValue ?? string.Empty, originalValue ?? string.Empty, StringComparison.Ordinal);
        }

        private void SetDraftValue(string key, string originalValue, string newValue)
        {
            originalValue = originalValue ?? string.Empty;
            newValue = newValue ?? string.Empty;

            if (string.Equals(newValue, originalValue, StringComparison.Ordinal))
            {
                draftStringValues.Remove(key);
                return;
            }

            draftStringValues[key] = newValue;
        }

        private bool HasDraftsWithPrefix(string prefix)
        {
            foreach (string key in draftStringValues.Keys)
            {
                if (key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private void ClearDraftsWithPrefix(string prefix)
        {
            var keysToRemove = new List<string>();

            foreach (string key in draftStringValues.Keys)
            {
                if (key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    keysToRemove.Add(key);
                }
            }

            for (int i = 0; i < keysToRemove.Count; i++)
            {
                draftStringValues.Remove(keysToRemove[i]);
            }
        }

        private void ClearDrafts()
        {
            draftStringValues.Clear();
        }

        private static string GetFacebookDraftKey(int index, string fieldName)
        {
            return FacebookDraftPrefix + index + "." + fieldName;
        }

        private static string GetGameAnalyticsDraftKey(RuntimePlatform platform, string fieldName)
        {
            return GameAnalyticsDraftPrefix + platform + "." + fieldName;
        }

        private string[] BuildFacebookAppPopupLabels(
            SerializedProperty appLabels,
            SerializedProperty appIds)
        {
            int count = Mathf.Max(1, Mathf.Max(appLabels.arraySize, appIds.arraySize));
            string[] labels = new string[count];

            for (int i = 0; i < count; i++)
            {
                string nameOriginal = GetStringArrayValue(appLabels, i);
                string idOriginal = GetStringArrayValue(appIds, i);

                string name = GetDraftValue(GetFacebookDraftKey(i, "appName"), nameOriginal);
                string id = GetDraftValue(GetFacebookDraftKey(i, "appId"), idOriginal);

                if (string.IsNullOrEmpty(name))
                {
                    name = $"App #{i + 1}";
                }

                labels[i] = string.IsNullOrEmpty(id)
                    ? name
                    : $"{name} ({id})";
            }

            return labels;
        }

        private void SaveFacebookSettings()
        {
            if (facebookSettings == null)
            {
                return;
            }

            var so = new SerializedObject(facebookSettings);
            so.Update();

            SerializedProperty appLabels = so.FindProperty("appLabels");
            SerializedProperty appIds = so.FindProperty("appIds");
            SerializedProperty clientTokens = so.FindProperty("clientTokens");
            SerializedProperty selectedAppIndex = so.FindProperty("selectedAppIndex");

            if (appLabels == null || appIds == null || clientTokens == null)
            {
                EditorUtility.DisplayDialog(
                    "Facebook Settings Save Failed",
                    "Required FacebookSettings fields were not found. The SDK may have changed its serialized field names.",
                    "OK"
                );
                return;
            }

            int selectedIndex = Mathf.Max(0, facebookSelectedAppIndex);

            if (selectedAppIndex != null)
            {
                selectedAppIndex.intValue = selectedIndex;
            }

            var keys = new List<string>(draftStringValues.Keys);

            for (int i = 0; i < keys.Count; i++)
            {
                string key = keys[i];

                int index;
                string fieldName;
                if (!TryParseFacebookDraftKey(key, out index, out fieldName))
                {
                    continue;
                }

                string value = draftStringValues[key];

                if (fieldName == "appName")
                {
                    SetStringArrayValue(appLabels, index, value);
                }
                else if (fieldName == "appId")
                {
                    SetStringArrayValue(appIds, index, value);
                }
                else if (fieldName == "clientToken")
                {
                    SetStringArrayValue(clientTokens, index, value);
                }
            }

            so.ApplyModifiedProperties();
            SaveAsset(facebookSettings);

            ClearDraftsWithPrefix(FacebookDraftPrefix);

            // Many Facebook SDK versions regenerate AndroidManifest when App ID or Client Token changes.
            // If the internal SDK class exists, call it. Otherwise, silently skip it.
            InvokeFacebookManifestGenerate();

            Debug.Log("[First Stage Settings] Facebook settings saved.");
        }

        private void SaveGameAnalyticsSettings()
        {
            if (gameAnalyticsSettings == null)
            {
                return;
            }

            var so = new SerializedObject(gameAnalyticsSettings);
            so.Update();

            SerializedProperty gameKeys = so.FindProperty("gameKey");
            SerializedProperty secretKeys = so.FindProperty("secretKey");
            SerializedProperty platforms = so.FindProperty("Platforms");

            if (gameKeys == null || secretKeys == null || platforms == null)
            {
                EditorUtility.DisplayDialog(
                    "GameAnalytics Settings Save Failed",
                    "Required GameAnalytics Settings fields were not found. The SDK may have changed the gameKey / secretKey / Platforms field names.",
                    "OK"
                );
                return;
            }

            ApplyGameAnalyticsDraftsForPlatform(gameKeys, secretKeys, platforms, RuntimePlatform.Android);
            ApplyGameAnalyticsDraftsForPlatform(gameKeys, secretKeys, platforms, RuntimePlatform.IPhonePlayer);

            so.ApplyModifiedProperties();
            SaveAsset(gameAnalyticsSettings);

            ClearDraftsWithPrefix(GameAnalyticsDraftPrefix);

            Debug.Log("[First Stage Settings] GameAnalytics settings saved.");
        }

        private void ApplyGameAnalyticsDraftsForPlatform(
            SerializedProperty gameKeys,
            SerializedProperty secretKeys,
            SerializedProperty platforms,
            RuntimePlatform platform)
        {
            int index = FindRuntimePlatformIndex(platforms, platform);
            if (index < 0)
            {
                return;
            }

            string gameKeyDraftKey = GetGameAnalyticsDraftKey(platform, "gameKey");
            string secretKeyDraftKey = GetGameAnalyticsDraftKey(platform, "secretKey");

            string value;
            if (draftStringValues.TryGetValue(gameKeyDraftKey, out value))
            {
                SetStringArrayValue(gameKeys, index, value);
            }

            if (draftStringValues.TryGetValue(secretKeyDraftKey, out value))
            {
                SetStringArrayValue(secretKeys, index, value);
            }
        }

        private static bool TryParseFacebookDraftKey(string key, out int index, out string fieldName)
        {
            index = -1;
            fieldName = string.Empty;

            if (string.IsNullOrEmpty(key) || !key.StartsWith(FacebookDraftPrefix, StringComparison.Ordinal))
            {
                return false;
            }

            string rest = key.Substring(FacebookDraftPrefix.Length);
            int dotIndex = rest.IndexOf('.');
            if (dotIndex <= 0 || dotIndex >= rest.Length - 1)
            {
                return false;
            }

            if (!int.TryParse(rest.Substring(0, dotIndex), out index))
            {
                index = -1;
                return false;
            }

            fieldName = rest.Substring(dotIndex + 1);
            return true;
        }

        static void SaveAsset(UnityObject asset)
        {
            if (asset == null)
            {
                return;
            }

            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        void OpenFacebookSettingsMenuOrShowMissingModuleDialog()
        {
            if (!SettingsValidationHelper.OpenFacebookSettingsMenu())
            {
                ShowModuleNotFoundDialog(
                    "Facebook SDK",
                    SettingsValidationHelper.GetFacebookSettingsMenuPathLabel()
                );
                return;
            }

            ReloadAssetsAndRepaint();
            EditorApplication.delayCall += ReloadAssetsAndRepaint;
        }

        void OpenGameAnalyticsSettingsMenuOrShowMissingModuleDialog()
        {
            if (!SettingsValidationHelper.OpenGameAnalyticsSettingsMenu())
            {
                ShowModuleNotFoundDialog(
                    "GameAnalytics SDK",
                    SettingsValidationHelper.GetGameAnalyticsSettingsMenuPathLabel()
                );
                return;
            }

            ReloadAssetsAndRepaint();
            EditorApplication.delayCall += ReloadAssetsAndRepaint;
        }

        void ReloadAssetsAndRepaint()
        {
            ReloadAssets();
            Repaint();
        }

        static void ShowModuleNotFoundDialog(string moduleName, string expectedMenuPath)
        {
            EditorUtility.DisplayDialog
            (
                moduleName + " Module Not Found",
                moduleName + " settings menu could not be found.\n\n" +
                "Expected menu path:\n" +
                expectedMenuPath + "\n\n" +
                "Please make sure the " + moduleName + " SDK is installed and compiled successfully.",
                "OK"
            );
        }

        static void ValidateFacebookSettings(UnityObject settings)
        {
            var result = SettingsValidationHelper.ValidateFacebookAndWait(
                settings,
                "Validating Facebook Settings",
                true
            );

            SettingsValidationHelper.ShowResultDialog(
                "Facebook Settings Validation",
                result
            );
        }

        static void ValidateGameAnalyticsSettings(UnityObject settings)
        {
            var result = SettingsValidationHelper.ValidateGameAnalyticsAndWait(
                settings,
                SettingsValidationHelper.GameAnalyticsPlatformValidationMode.AllMobilePlatforms,
                null,
                "Validating GameAnalytics Settings",
                true
            );

            SettingsValidationHelper.ShowResultDialog(
                "GameAnalytics Settings Validation",
                result
            );
        }

        static bool EnsureStringArraySize(
            SerializedProperty array,
            int size,
            string defaultValue)
        {
            if (array == null || !array.isArray)
                return false;

            bool changed = false;

            while (array.arraySize < size)
            {
                int index = array.arraySize;
                array.InsertArrayElementAtIndex(index);

                var element = array.GetArrayElementAtIndex(index);
                if (element.propertyType == SerializedPropertyType.String)
                {
                    element.stringValue = defaultValue;
                }

                changed = true;
            }

            return changed;
        }

        private static int FindRuntimePlatformIndex(
            SerializedProperty platforms,
            RuntimePlatform platform)
        {
            if (platforms == null || !platforms.isArray)
            {
                return -1;
            }

            string targetName = platform.ToString();
            int targetValue = (int)platform;

            for (int i = 0; i < platforms.arraySize; i++)
            {
                var element = platforms.GetArrayElementAtIndex(i);

                if (element.propertyType == SerializedPropertyType.Enum)
                {
                    int enumIndex = element.enumValueIndex;

                    if (enumIndex >= 0 && enumIndex < element.enumNames.Length)
                    {
                        if (element.enumNames[enumIndex] == targetName)
                        {
                            return i;
                        }
                    }

                    if (enumIndex >= 0 && enumIndex < element.enumDisplayNames.Length)
                    {
                        if (element.enumDisplayNames[enumIndex] == targetName)
                        {
                            return i;
                        }
                    }
                }
                else if (element.propertyType == SerializedPropertyType.Integer)
                {
                    if (element.intValue == targetValue)
                    {
                        return i;
                    }
                }
            }

            return -1;
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

        private static void SetStringArrayValue(SerializedProperty array, int index, string value)
        {
            if (array == null || !array.isArray || index < 0)
            {
                return;
            }

            EnsureStringArraySize(array, index + 1, string.Empty);

            SerializedProperty element = array.GetArrayElementAtIndex(index);
            if (element != null && element.propertyType == SerializedPropertyType.String)
            {
                element.stringValue = value ?? string.Empty;
            }
        }

        static bool InvokeGameAnalyticsAddPlatform(
            UnityObject settings,
            RuntimePlatform platform)
        {
            if (settings == null)
            {
                return false;
            }

            MethodInfo method = settings.GetType().GetMethod(
                "AddPlatform",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
            );

            if (method == null)
            {
                return false;
            }

            Undo.RecordObject(settings, $"Add GameAnalytics {platform}");
            method.Invoke(settings, new object[] { platform });
            EditorUtility.SetDirty(settings);
            return true;
        }

        static void AddGameAnalyticsPlatformFallback(
            UnityObject settings,
            RuntimePlatform platform)
        {
            var so = new SerializedObject(settings);
            so.Update();

            var platforms = so.FindProperty("Platforms");
            if (platforms == null || !platforms.isArray)
            {
                return;
            }

            int index = platforms.arraySize;
            platforms.InsertArrayElementAtIndex(index);
            SetRuntimePlatformElement(platforms.GetArrayElementAtIndex(index), platform);

            EnsureStringArraySize(so.FindProperty("gameKey"), index + 1, string.Empty);
            EnsureStringArraySize(so.FindProperty("secretKey"), index + 1, string.Empty);
            EnsureStringArraySize(so.FindProperty("Build"), index + 1, "0.1");

            EnsureStringArraySize(so.FindProperty("SelectedPlatformOrganization"), index + 1, string.Empty);
            EnsureStringArraySize(so.FindProperty("SelectedPlatformStudio"), index + 1, string.Empty);
            EnsureStringArraySize(so.FindProperty("SelectedPlatformGame"), index + 1, string.Empty);

            EnsureIntArraySize(so.FindProperty("SelectedPlatformGameID"), index + 1, -1);
            EnsureIntArraySize(so.FindProperty("SelectedOrganization"), index + 1, 0);
            EnsureIntArraySize(so.FindProperty("SelectedStudio"), index + 1, 0);
            EnsureIntArraySize(so.FindProperty("SelectedGame"), index + 1, 0);

            EnsureBoolArraySize(so.FindProperty("PlatformFoldOut"), index + 1, true);

            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(settings);
        }

        static bool EnsureIntArraySize(
            SerializedProperty array,
            int size,
            int defaultValue)
        {
            if (array == null || !array.isArray)
            {
                return false;
            }

            bool changed = false;

            while (array.arraySize < size)
            {
                int index = array.arraySize;
                array.InsertArrayElementAtIndex(index);

                var element = array.GetArrayElementAtIndex(index);
                if (element.propertyType == SerializedPropertyType.Integer)
                {
                    element.intValue = defaultValue;
                }

                changed = true;
            }

            return changed;
        }

        private static bool EnsureBoolArraySize(
            SerializedProperty array,
            int size,
            bool defaultValue)
        {
            if (array == null || !array.isArray)
            {
                return false;
            }

            bool changed = false;

            while (array.arraySize < size)
            {
                int index = array.arraySize;
                array.InsertArrayElementAtIndex(index);

                var element = array.GetArrayElementAtIndex(index);
                if (element.propertyType == SerializedPropertyType.Boolean)
                {
                    element.boolValue = defaultValue;
                }

                changed = true;
            }

            return changed;
        }

        static void SetRuntimePlatformElement(
            SerializedProperty element,
            RuntimePlatform platform)
        {
            if (element.propertyType == SerializedPropertyType.Enum)
            {
                string targetName = platform.ToString();

                for (int i = 0; i < element.enumNames.Length; i++)
                {
                    if (element.enumNames[i] == targetName)
                    {
                        element.enumValueIndex = i;
                        return;
                    }
                }
            }

            if (element.propertyType == SerializedPropertyType.Integer)
            {
                element.intValue = (int)platform;
            }
        }

        static void InvokeFacebookManifestGenerate()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;

                try
                {
                    types = assembly.GetTypes();
                }
                catch
                {
                    continue;
                }

                foreach (var type in types)
                {
                    if (type.Name != "ManifestMod")
                    {
                        continue;
                    }

                    var method = type.GetMethod(
                        "GenerateManifest",
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic
                    );

                    if (method == null)
                    {
                        continue;
                    }

                    try
                    {
                        method.Invoke(null, null);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning("[First Stage Settings] Facebook ManifestMod.GenerateManifest failed: " + ex.Message);
                    }

                    return;
                }
            }
        }
    }
}
