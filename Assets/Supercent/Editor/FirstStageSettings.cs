using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityObject = UnityEngine.Object;

// UPDATED 2026-06-18: Validation is delegated to SettingsValidationHelper. Missing settings assets expose plugin menu buttons instead of auto-creation.
namespace Supercent.Edit
{
    public class FirstStageSettings : EditorWindow
    {
        private static GUIStyle sectionTitleStyle;
        private static GUIStyle platformTitleStyle;
        private UnityObject facebookSettings;
        private UnityObject gameAnalyticsSettings;

        private Vector2 scroll;

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

        [MenuItem("Supercent/First Stage Settings")]
        static void Open() => GetWindow<FirstStageSettings>("First Stage Settings");

        void OnEnable() => ReloadAssets();

        void OnGUI()
        {
            EditorGUILayout.Space(6);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Reload Settings Assets", GUILayout.Height(26)))
                {
                    ReloadAssets();
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
        }

        private void DrawFacebookSettings()
        {
            DrawSectionTitle("Facebook Settings");

            using (new EditorGUILayout.VerticalScope("box"))
            {
                DrawAssetRow("FacebookSettings Asset", ref facebookSettings);

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

                bool changed = false;

                int count = Mathf.Max(
                    1,
                    Mathf.Max(appLabels.arraySize, Mathf.Max(appIds.arraySize, clientTokens.arraySize))
                );

                changed |= EnsureStringArraySize(appLabels, count, "New App");
                changed |= EnsureStringArraySize(appIds, count, "0");
                changed |= EnsureStringArraySize(clientTokens, count, string.Empty);

                int index = 0;
                if (selectedAppIndex != null)
                {
                    index = Mathf.Clamp(selectedAppIndex.intValue, 0, count - 1);

                    if (count > 1)
                    {
                        EditorGUI.BeginChangeCheck();
                        index = EditorGUILayout.Popup("Selected App", index, BuildFacebookAppPopupLabels(appLabels, appIds));
                        if (EditorGUI.EndChangeCheck())
                        {
                            selectedAppIndex.intValue = index;
                            changed = true;
                        }
                    }

                    selectedAppIndex.intValue = index;
                }

                EditorGUILayout.Space(4);

                SerializedProperty appNameProp = appLabels.GetArrayElementAtIndex(index);
                SerializedProperty appIdProp = appIds.GetArrayElementAtIndex(index);
                SerializedProperty clientTokenProp = clientTokens.GetArrayElementAtIndex(index);

                EditorGUI.BeginChangeCheck();

                appNameProp.stringValue = EditorGUILayout.TextField("App Name", appNameProp.stringValue);
                appIdProp.stringValue = EditorGUILayout.TextField("Facebook App ID", appIdProp.stringValue);
                clientTokenProp.stringValue = EditorGUILayout.TextField("Client Token", clientTokenProp.stringValue);

                if (EditorGUI.EndChangeCheck())
                {
                    changed = true;
                }

                if (changed)
                {
                    so.ApplyModifiedProperties();
                    EditorUtility.SetDirty(facebookSettings);
                }
                else
                {
                    so.ApplyModifiedProperties();
                }

                EditorGUILayout.Space(6);

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Save Facebook Settings", GUILayout.Height(26)))
                    {
                        SaveAsset(facebookSettings);

                        // Many Facebook SDK versions regenerate AndroidManifest when App ID or Client Token changes.
                        // If the internal SDK class exists, call it. Otherwise, silently skip it.
                        InvokeFacebookManifestGenerate();

                        Debug.Log("[First Stage Settings] Facebook settings saved.");
                    }

                    if (GUILayout.Button("Validate Facebook Settings", GUILayout.Height(26)))
                    {
                        ValidateFacebookSettings(facebookSettings);
                    }
                }
            }
        }

        void DrawGameAnalyticsSettings()
        {
            DrawSectionTitle("GameAnalytics Settings");

            using (new EditorGUILayout.VerticalScope("box"))
            {
                DrawAssetRow("GameAnalytics Settings Asset", ref gameAnalyticsSettings);

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

                bool changed = false;

                changed |= EnsureStringArraySize(gameKeys, platforms.arraySize, string.Empty);
                changed |= EnsureStringArraySize(secretKeys, platforms.arraySize, string.Empty);

                changed |= DrawGameAnalyticsPlatformKeys(
                    so,
                    gameKeys,
                    secretKeys,
                    platforms,
                    RuntimePlatform.Android,
                    "Android"
                );

                EditorGUILayout.Space(8);

                changed |= DrawGameAnalyticsPlatformKeys(
                    so,
                    gameKeys,
                    secretKeys,
                    platforms,
                    RuntimePlatform.IPhonePlayer,
                    "iOS"
                );

                if (changed)
                {
                    so.ApplyModifiedProperties();
                    EditorUtility.SetDirty(gameAnalyticsSettings);
                }
                else
                {
                    so.ApplyModifiedProperties();
                }

                EditorGUILayout.Space(6);

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Save GameAnalytics Settings", GUILayout.Height(26)))
                    {
                        SaveAsset(gameAnalyticsSettings);
                        Debug.Log("[First Stage Settings] GameAnalytics settings saved.");
                    }

                    if (GUILayout.Button("Validate GameAnalytics Settings", GUILayout.Height(26)))
                    {
                        ValidateGameAnalyticsSettings(gameAnalyticsSettings);
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

        private static bool DrawGameAnalyticsPlatformKeys(
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

                return false;
            }

            EnsureStringArraySize(gameKeys, index + 1, string.Empty);
            EnsureStringArraySize(secretKeys, index + 1, string.Empty);

            var gameKeyProp = gameKeys.GetArrayElementAtIndex(index);
            var secretKeyProp = secretKeys.GetArrayElementAtIndex(index);

            EditorGUI.BeginChangeCheck();

            gameKeyProp.stringValue = EditorGUILayout.TextField($"{label} Game Key", gameKeyProp.stringValue);
            secretKeyProp.stringValue = EditorGUILayout.TextField($"{label} Secret Key", secretKeyProp.stringValue);

            return EditorGUI.EndChangeCheck();
        }

        private static void DrawAssetRow(string label, ref UnityObject asset)
        {
            asset = EditorGUILayout.ObjectField(label, asset, typeof(ScriptableObject), false);

            if (asset != null)
            {
                string path = AssetDatabase.GetAssetPath(asset);
                string displayPath = string.IsNullOrEmpty(path) ? "(unsaved object)" : path;

                // Read-only display only. Do not use SelectableLabel or TextField here.
                EditorGUILayout.LabelField("Asset Path", displayPath);
            }
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

        static string[] BuildFacebookAppPopupLabels(
            SerializedProperty appLabels,
            SerializedProperty appIds)
        {
            int count = Mathf.Max(appLabels.arraySize, appIds.arraySize);
            string[] labels = new string[count];

            for (int i = 0; i < count; i++)
            {
                string name = i < appLabels.arraySize
                    ? appLabels.GetArrayElementAtIndex(i).stringValue
                    : string.Empty;

                string id = i < appIds.arraySize
                    ? appIds.GetArrayElementAtIndex(i).stringValue
                    : string.Empty;

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
