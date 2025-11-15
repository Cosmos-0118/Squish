# Automation Samples

This guide captures working examples that exercise the new cross-app automation host shipped in Phase 12. The host listens on `http://localhost:<port>/automation/` (defaults to `11843`) with a bearer token guard pulled from `SettingsRegistry.AutomationAccessToken`.

## Power Automate Desktop

1. Set `EnableCrossAppAutomation` to `true` in Settings or via `%AppData%/Clop/config.json` and pick an access token.
2. Create a Flow with an **HTTP** action configured as:
   - **Method**: `POST`
   - **URL**: `http://localhost:11843/automation/power-automate/optimise`
   - **Headers**:
     - `Authorization: Bearer <your-token>`
     - `Content-Type: application/json`
   - **Body**:

```json
{
  "paths": ["C:/Users/Ada/Pictures"],
  "aggressive": false,
  "recursive": true,
  "source": "power-automate"
}
```

3. Trigger the Flow to push work into the background service. Responses mirror `OptimisationResult` payloads and include request IDs for progress correlation.

## Windows Share Targets

The `/automation/share/optimise` endpoint accepts the shape used by Windows share intents. A minimal example payload:

```json
{
  "items": [
    {
      "path": "C:/Users/Ada/Desktop/demo.png",
      "type": "image"
    }
  ],
  "source": "windows-share"
}
```

POST the JSON with the same bearer token header to immediately run the optimiser.

## Teams Adaptive Cards

When `EnableTeamsAdaptiveCards` is true the `CrossAppAutomationHost` emits rich responses that Teams can render inline. Use the `/automation/teams/optimise` endpoint with the following body:

```json
{
  "paths": ["C:/Users/Ada/Videos/clip.mov"],
  "aggressive": true,
  "notify": {
    "channelId": "19:abc123@thread.tacv2",
    "tenantId": "00000000-0000-0000-0000-000000000000"
  }
}
```

The response includes an `adaptiveCard` object that you can forward to a Teams webhook or Flow "Post adaptive card" action.

## Shortcuts / Named Pipe Bridge

Existing Windows Shortcuts continue to route through `ShortcutsBridge`, which now reuses the shared `AutomationTargetResolver`. Payload formats documented above apply equally to named pipe clients.
