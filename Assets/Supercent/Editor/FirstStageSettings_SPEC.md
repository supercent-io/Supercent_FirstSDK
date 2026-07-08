# First Stage Settings Validation Spec

## Purpose

`FirstStageSettings` is a Unity EditorWindow for editing first-stage third-party SDK settings used by the project.

It currently manages:

- Facebook SDK settings
  - App Name
  - Facebook App ID
  - Client Token
- GameAnalytics SDK settings
  - Android Game Key
  - Android Secret Key
  - iOS Game Key
  - iOS Secret Key

Validation is centralized in `SettingsValidationHelper` so both the editor UI and the pre-build validator use the same rules.

## Files

| File | Role |
|---|---|
| `FirstStageSettings.cs` | EditorWindow UI for editing and saving Facebook/GameAnalytics settings. |
| `SettingsValidationHelper.cs` | Shared async validation helper. Reads Unity assets into snapshots, runs validation, and provides synchronous wait wrappers. |
| `SettingsPreBuildValidator.cs` | Unity pre-build hook. Calls the validation helper before player builds and stops the build on validation errors. |

## Menu

The editor window is opened from:

```text
Supercent > First Stage Settings
```

## Asset paths

Default lookup paths:

```text
Assets/FacebookSDK/SDK/Resources/FacebookSettings.asset
Assets/Resources/GameAnalytics/Settings.asset
```

If the default path fails, the helper searches ScriptableObject assets by expected type:

```text
Facebook.Unity.Settings.FacebookSettings
GameAnalyticsSDK.Setup.Settings
```

The helper does not auto-create missing settings assets during reload or validation. Missing assets remain `null` for editing purposes. For Android target-specific build validation, Facebook values are validated from the actual Android Manifest instead of the FacebookSettings asset. For iOS target-specific validation, Facebook values are read from `FacebookSettings.asset` because the final `Info.plist` is generated inside the Xcode project during the Unity iOS build. In the EditorWindow only, a missing asset exposes a find button that calls the SDK menu:

```text
Facebook/Edit Settings
Window/GameAnalytics/Select Settings
```

## Facebook platform value lookup

Android target-specific Facebook validation reads the actual manifest values that will be used by the Android build. iOS target-specific Facebook validation reads `FacebookSettings.asset` values before build, because `Info.plist` does not generally exist until Unity creates the Xcode project.

Android lookup checks:

```text
Assets/Plugins/Android/AndroidManifest.xml
Assets/Plugins/Android/AndroidManifest
```

If those files are missing, the helper also searches under `Assets/Plugins/Android` for `AndroidManifest*`. The Android manifest fields are:

```xml
<meta-data android:name="com.facebook.sdk.ApplicationId" android:value="..." />
<meta-data android:name="com.facebook.sdk.ClientToken" android:value="..." />
```

`@string/...` references are resolved from Android string resource files under:

```text
Assets/Plugins/Android/**/res/values/*.xml
Assets/FacebookSDK/Plugins/Android/**/res/values/*.xml
```

iOS lookup uses the selected entry in:

```text
Assets/FacebookSDK/SDK/Resources/FacebookSettings.asset
```

The helper reads the same serialized fields used by the editor UI:

```text
appLabels
appIds
clientTokens
selectedAppIndex
```

## UI rules

- All button names and help messages are English.
- Section titles are intentionally larger than normal labels.
- Section title layout uses explicit control height to avoid clipping against the divider line.
- Asset paths are displayed as read-only `LabelField` text.
- There is no separate original-file select button.
- Section-level validation buttons are not shown. Build validation is handled by the two bottom buttons.
- Bottom validation buttons call `SettingsValidationHelper` for Google(Android) and iOS separately.
- Editable values are staged locally in the EditorWindow and are not written to assets until Save is clicked.
- Fields with unsaved local changes are displayed with a bold highlighted label.
- Save buttons remain inside each settings section. Build validation buttons are placed at the bottom of the window.
- `Reload Settings Assets` discards all staged local changes and reloads the latest asset values.
- If a settings asset is missing, the section shows a `Find ... Settings File` button.
- The find button opens the plugin-provided settings menu instead of creating assets directly.
- If the plugin settings menu cannot be executed, the UI shows a module-not-found dialog.


## Draft editing flow

The EditorWindow no longer writes field edits directly into `SerializedObject` during `OnGUI`.

Current behavior:

1. If a field has no local draft value, it displays the current value from the asset every GUI refresh.
2. When the user edits a field, the new value is stored in an EditorWindow-local draft dictionary.
3. The asset is not marked dirty and `SerializedObject.ApplyModifiedProperties()` is not called for normal text edits.
4. Dirty fields are identified by comparing the draft value with the current asset value.
5. Dirty fields are displayed with a bold highlighted label.
6. Clicking Save applies only the staged values to the corresponding asset.
7. Clicking Reload clears all staged draft values and reloads the latest asset references.

Build validation buttons validate saved GameAnalytics asset data through `SettingsValidationHelper`. Facebook build validation reads Android values from the actual Android Manifest and iOS values from `FacebookSettings.asset`. Unsaved draft values are only included after Save is clicked; for Android, the generated manifest must also be refreshed after asset changes.

## Missing settings asset flow

When `FacebookSettings.asset` or `GameAnalytics Settings.asset` is missing:

1. The corresponding `ObjectField` remains assignable manually.
2. A warning HelpBox is shown.
3. A `Find ... Settings File` button is shown.
4. The button executes the plugin's official settings menu through `EditorApplication.ExecuteMenuItem`.
5. If the menu item cannot be executed, a module-not-found popup is shown.
6. If the menu opens successfully, assets are refreshed and the EditorWindow reloads the setting references immediately and once more through `EditorApplication.delayCall`.

This intentionally avoids direct ScriptableObject fallback creation because SDK menu behavior is safer across plugin versions.

## Validation flow

### EditorWindow validation

`FirstStageSettings` calls:

```csharp
SettingsValidationHelper.ValidateAllAndWait(...)
```

The bottom buttons pass one build target at a time:

| Button | Build target | FirstStep API `os` |
|---|---|---|
| `Validate Google Build Settings` | `BuildTarget.Android` | `android` |
| `Validate iOS Build Settings` | `BuildTarget.iOS` | `ios` |

Then it displays the result through:

```csharp
SettingsValidationHelper.ShowResultDialog(...)
```

The dialog title includes `Success` or `Failed`, and the message includes the remote API summary, `checks`, and concrete `errors` when `data.status == "attention"`. EditorWindow validation uses `/api/v1/firststep/validate`.

The validation itself is async internally, but the caller waits until the result is available.

### Pre-build validation

`SettingsPreBuildValidator` implements:

```csharp
IPreprocessBuildWithReport.OnPreprocessBuild(BuildReport report)
```

Before the build starts, it calls:

```csharp
SettingsValidationHelper.ValidateAllAndWait(...)
```

If the result contains errors, it first displays the detailed validation result through:

```csharp
SettingsValidationHelper.ShowResultDialog(
    "First Stage Settings Pre-Build Validation",
    result
);
```

The dialog contains the same detail as the editor validation buttons, including failed `checks` values and each remote API `errors` item when `data.status == "attention"`. In Unity batch mode the dialog is skipped so CI/build automation does not block.

After the dialog/log output, it throws:

```csharp
BuildFailedException
```

This stops the build before player generation continues. Pre-build FirstStep API validation uses `/api/v1/firststep/build/register` instead of `/api/v1/firststep/validate`.

## Async design

The helper separates validation into two phases:

1. Read Unity assets synchronously on the editor/main thread.
2. Validate plain snapshot data asynchronously.

This is intentional because future validation may call remote APIs.

Remote API calls are added inside the shared validation helper. The FirstStep API uses the same request/response payload for both endpoints:

```text
EditorWindow validation:
POST {FirstStepValidateApiDomain}/api/v1/firststep/validate

Pre-build validation:
POST {FirstStepValidateApiDomain}/api/v1/firststep/build/register

Content-Type: application/json
x-api-key: FirstStepValidateApiKey
```

The request body is serialized with `JsonUtility.ToJson(...)`.

The response JSON is parsed in `ValidateFirstStepApiAsync(...)`. The parser uses `JsonUtility.FromJson<T>(...)` after normalizing dotted `checks` keys such as `ga.gameKey` and `fb.appId` into C#-safe field names.

Additional API calls can be added inside these async methods:

```csharp
ValidateFacebookSnapshotAsync(...)
ValidateGameAnalyticsSnapshotAsync(...)
ValidateFirstStepApiAsync(...)
```

Important rule for future awaits:

```csharp
await SomeApiCallAsync(...).ConfigureAwait(false);
```

This reduces the risk of deadlock when the pre-build hook waits synchronously for validation to finish.

## Current validation rules

### Facebook

When a mobile build target is supplied, Facebook validation uses platform-appropriate source values:

- Android: actual `AndroidManifest` values.
- iOS: selected `FacebookSettings.asset` values.

The FirstStep API payload uses the same source as local validation for each platform.

Android errors when:

- AndroidManifest is missing from the lookup paths.
- Manifest cannot be read or parsed.
- `com.facebook.sdk.ApplicationId` meta-data is missing.
- `com.facebook.sdk.ClientToken` meta-data is missing.
- `@string/...` references cannot be resolved.
- Facebook ApplicationId is empty, `0`, `fb0`, or has an invalid format.
- Facebook Client Token is empty.

iOS errors when the selected `FacebookSettings.asset` entry is invalid:

- `FacebookSettings.asset` is missing.
- Serialized fields are missing: `appLabels`, `appIds`, or `clientTokens`.
- No Facebook app entry exists.
- Selected App Name is empty.
- Selected Facebook App ID is empty, `0`, or has an invalid format.
- Selected Client Token is empty.

When no mobile build target is supplied, the helper also uses the asset-based Facebook checks:

- `FacebookSettings.asset` is missing.
- Serialized fields are missing:
  - `appLabels`
  - `appIds`
  - `clientTokens`
- No Facebook app entry exists.
- Selected App Name is empty.
- Selected Facebook App ID is empty or `0`.
- Selected Facebook App ID contains non-digit characters.
- Selected Client Token is empty.

### GameAnalytics

Errors when:

- `Settings.asset` is missing.
- Serialized fields are missing:
  - `gameKey`
  - `secretKey`
  - `Platforms`
- Required platform entry is missing.
- Required platform Game Key entry is missing.
- Required platform Secret Key entry is missing.
- Required platform Game Key is empty.
- Required platform Secret Key is empty.

EditorWindow build validation checks only the platform selected by the pressed bottom button.

GameAnalytics errors use platform-specific sources such as `GameAnalytics Android` and `GameAnalytics iOS` so empty AOS/iOS values are reported independently.

Pre-build GameAnalytics validation checks only the platform matching the current build target:

| Build target | Required GameAnalytics platform |
|---|---|
| Android | Android |
| iOS | iOS / `RuntimePlatform.IPhonePlayer` |
| Other | No target-specific GameAnalytics check |

Facebook validation is always enabled in the pre-build validator.
Player build settings and FirstStep API validation are enabled in the pre-build validator only for Android and iOS builds.

### Player build settings

Errors when:

- Build target is not specified.
- Build target is not Android or iOS when First Stage build validation is requested.
- Platform package name / bundle identifier is empty.
- Game version is empty.
- Android bundle version code / iOS build number is empty.
- Android bundle version code is not a positive integer.

### FirstStep validation API

The API request payload is:

```json
{
  "packageName": "PlayerSettings application identifier for the selected build target",
  "os": "android | ios",
  "gaGameKey": "GameAnalytics Game Key for the selected build target",
  "gaSecretKey": "GameAnalytics Secret Key for the selected build target",
  "fbAppId": "Facebook App ID from AndroidManifest on Android or FacebookSettings.asset on iOS; Android fb{appId} values are sent without the fb prefix",
  "fbClientToken": "Facebook Client Token from AndroidManifest on Android or FacebookSettings.asset on iOS",
  "gameVersion": "PlayerSettings.bundleVersion",
  "bundleVersionCode": "Android bundleVersionCode or iOS buildNumber",
  "sdkVersion": "1.0.0"
}
```

The expected response body is:

```json
{
  "status": "success",
  "message": null,
  "data": {
    "valid": false,
    "status": "attention",
    "source": "validate",
    "checkedAt": "2026-07-07T07:00:00.000Z",
    "checks": {
      "packageName": "ok",
      "ga.gameKey": "ok",
      "ga.secretKey": "mismatch",
      "fb.appId": "ok",
      "fb.clientToken": "ok",
      "keyHash": "ok"
    },
    "errors": [
      {
        "field": "ga.secretKey",
        "code": "GA_SECRET_MISMATCH",
        "message": "등록된 게임과 일치하지 않아요 — 대시보드에서 Secret Key를 다시 복사하세요."
      }
    ]
  }
}
```

Response contract details:

`data.status` values:

| value | Meaning |
|---|---|
| `pass` | Remote validation passed. |
| `attention` | Remote validation failed. The user must check SDK setting values. |

`data.source` values:

| Endpoint | Expected value |
|---|---|
| `/firststep/validate` | `validate` |
| `/firststep/build/register` | `build_register` |

`checks` keys:

| key | Description | Handling |
|---|---|---|
| `packageName` | Package Name / Bundle ID | Included in dialog feedback. |
| `ga.gameKey` | GA Game Key | Included in dialog feedback. |
| `ga.secretKey` | GA Secret Key | Included in dialog feedback. |
| `fb.appId` | Facebook App ID | Included in dialog feedback. |
| `fb.clientToken` | Facebook Client Token | Included in dialog feedback. |
| `keyHash` | Not an SDK setting validation target. | Ignored. |

`checks` values:

| value | Meaning |
|---|---|
| `ok` | Passed. |
| `missing` | Value is missing. |
| `invalid` | Invalid format. |
| `mismatch` | Does not match the PSL registered value. |

Known error codes:

| code | Meaning |
|---|---|
| `PACKAGE_INVALID` | Package Name / Bundle ID format is invalid. |
| `PACKAGE_MISMATCH` | Package Name / Bundle ID does not match PSL. |
| `GAME_NOT_FOUND` | PSL game was not found by `packageName + os`. |
| `GA_GAME_KEY_INVALID` | GA Game Key format is invalid. |
| `GA_GAME_KEY_MISMATCH` | GA Game Key does not match PSL. |
| `GA_SECRET_INVALID` | GA Secret Key format is invalid. |
| `GA_SECRET_MISMATCH` | GA Secret Key does not match PSL. |
| `FB_APP_ID_INVALID` | Facebook App ID format is invalid. |
| `FB_APP_ID_MISMATCH` | Facebook App ID does not match PSL. |
| `FB_CLIENT_TOKEN_MISSING` | Facebook Client Token is missing. |
| `FB_CLIENT_TOKEN_INVALID` | Facebook Client Token format is invalid. |
| `FB_CLIENT_TOKEN_MISMATCH` | Facebook Client Token does not match PSL. |

Response handling:

- HTTP status code must be exactly `200`. Any non-`200` response is treated as a communication issue, not a validation result.
- Empty or unparsable response bodies are treated as validation errors.
- Validation pass/fail is decided only by `data.status`.
- `data.status == "pass"` means remote validation passed.
- `data.status == "attention"` means remote validation failed. The dialog includes non-`ok` `checks` values and each item in `data.errors` as blocking validation errors.
- `data.checks` values are included in the validation result summary so the editor dialog shows which checks passed or failed.
- If `data.status == "attention"` but `data.errors` is empty, non-`ok` check values are used to create a fallback error message.
- `data.valid` may exist in the response DTO, but it is not used as the final pass/fail criterion.
- For `/firststep/validate`, `data.source` is expected to be `validate`; for `/firststep/build/register`, `data.source` is expected to be `build_register`. Unexpected values are reported as warnings.

Errors when:

- `FirstStepValidateApiDomain` is empty or invalid.
- `FirstStepValidateApiKey` is empty.
- Required local snapshots are unavailable.
- The HTTP call is canceled, times out, throws an exception, or returns anything other than HTTP `200`.
- The response body is empty, unparsable, has empty `data`, has unknown `data.status`, or returns `data.status == "attention"`.

The API call is skipped if earlier local validation already produced errors.

## Build failure behavior

If validation fails during pre-build:

1. Errors are logged with `Debug.LogError`.
2. `BuildFailedException` is thrown.
3. Unity stops the build.

Warnings and info messages do not stop the build unless later changed in the helper policy.

## Extension points

Add or update API checks here:

```csharp
// Facebook remote validation
ValidateFacebookSnapshotAsync(...)

// GameAnalytics remote validation
ValidateGameAnalyticsSnapshotAsync(...)

// FirstStep combined build validation API
ValidateFirstStepApiAsync(...)
```

Recommended pattern for this API family:

```csharp
private static async Task<bool> HasExpectedTransportStatusAsync(..., CancellationToken cancellationToken)
{
    var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
    return (int)response.StatusCode == 200;
}
```

Do not call Unity APIs after an awaited remote/API call unless execution is explicitly returned to the Unity main thread.
