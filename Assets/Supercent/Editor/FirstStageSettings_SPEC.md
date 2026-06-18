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

The helper does not auto-create missing settings assets during reload or validation. Missing assets remain `null` and are reported by validation. In the EditorWindow only, a missing asset exposes a find button that calls the SDK menu:

```text
Facebook/Edit Settings
Window/GameAnalytics/Select Settings
```

## UI rules

- All button names and help messages are English.
- Section titles are intentionally larger than normal labels.
- Section title layout uses explicit control height to avoid clipping against the divider line.
- Asset paths are displayed as read-only `LabelField` text.
- There is no separate original-file select button.
- Validation buttons call `SettingsValidationHelper`.
- If a settings asset is missing, the section shows a `Find ... Settings File` button.
- The find button opens the plugin-provided settings menu instead of creating assets directly.
- If the plugin settings menu cannot be executed, the UI shows a module-not-found dialog.

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
SettingsValidationHelper.ValidateFacebookAndWait(...)
SettingsValidationHelper.ValidateGameAnalyticsAndWait(...)
```

Then it displays the result through:

```csharp
SettingsValidationHelper.ShowResultDialog(...)
```

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

If the result contains errors, it throws:

```csharp
BuildFailedException
```

This stops the build before player generation continues.

## Async design

The helper separates validation into two phases:

1. Read Unity assets synchronously on the editor/main thread.
2. Validate plain snapshot data asynchronously.

This is intentional because future validation may call remote APIs.

Future API calls should be added inside these async methods:

```csharp
ValidateFacebookSnapshotAsync(...)
ValidateGameAnalyticsSnapshotAsync(...)
```

Important rule for future awaits:

```csharp
await SomeApiCallAsync(...).ConfigureAwait(false);
```

This reduces the risk of deadlock when the pre-build hook waits synchronously for validation to finish.

## Current validation rules

### Facebook

Errors when:

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

EditorWindow GameAnalytics validation checks both Android and iOS.

GameAnalytics errors use platform-specific sources such as `GameAnalytics Android` and `GameAnalytics iOS` so empty AOS/iOS values are reported independently.

Pre-build GameAnalytics validation checks only the platform matching the current build target:

| Build target | Required GameAnalytics platform |
|---|---|
| Android | Android |
| iOS | iOS / `RuntimePlatform.IPhonePlayer` |
| Other | No target-specific GameAnalytics check |

Facebook validation is always enabled in the pre-build validator.

## Build failure behavior

If validation fails during pre-build:

1. Errors are logged with `Debug.LogError`.
2. `BuildFailedException` is thrown.
3. Unity stops the build.

Warnings and info messages do not stop the build unless later changed in the helper policy.

## Extension points

Add API checks here:

```csharp
// Facebook remote validation
ValidateFacebookSnapshotAsync(...)

// GameAnalytics remote validation
ValidateGameAnalyticsSnapshotAsync(...)
```

Recommended pattern:

```csharp
private static async Task<bool> CheckRemoteAsync(..., CancellationToken cancellationToken)
{
    var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
    return response.IsSuccessStatusCode;
}
```

Do not call Unity APIs after an awaited remote/API call unless execution is explicitly returned to the Unity main thread.
