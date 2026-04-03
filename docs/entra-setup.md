# Entra App Registration Setup Guide

This guide walks through creating the Microsoft Entra ID (formerly Azure AD) app registration required for AFH Sync to access Microsoft Graph API.

## Prerequisites

- Access to the Azure Portal with Global Administrator or Application Administrator role
- The AFH tenant (Atlanta Fine Homes Sotheby's International Realty)

## Step 1: Create App Registration

1. Navigate to [Azure Portal](https://portal.azure.com)
2. Go to **Microsoft Entra ID** > **App registrations** > **New registration**
3. Fill in:
   - **Name:** `AFH Sync`
   - **Supported account types:** "Accounts in this organizational directory only" (Single tenant)
   - **Redirect URI:** Leave blank (daemon app, no user sign-in)
4. Click **Register**

## Step 2: Note Application IDs

After registration, you will see the **Overview** page. Record these values:

| Field | Config Key | Description |
|-------|-----------|-------------|
| **Application (client) ID** | `Graph:ClientId` | Unique identifier for the app |
| **Directory (tenant) ID** | `Graph:TenantId` | Your Entra tenant ID |

These values go into your `.env` file or `appsettings.json`.

## Step 3: Create Client Secret

1. Go to **Certificates & secrets** > **Client secrets** > **New client secret**
2. Fill in:
   - **Description:** `AFH Sync Production`
   - **Expires:** 24 months (recommended)
3. Click **Add**
4. **IMPORTANT:** Copy the **Value** immediately -- it is only shown once!

| Field | Config Key | Description |
|-------|-----------|-------------|
| **Secret Value** | `Graph:ClientSecret` | The client secret (copy immediately!) |

> **WARNING:** The secret value is only displayed once. If you lose it, you must create a new secret.

## Step 4: Configure API Permissions

1. Go to **API permissions** > **Add a permission**
2. Select **Microsoft Graph** > **Application permissions**
3. Add the following permissions:

| Permission | Purpose | Required For |
|-----------|---------|-------------|
| `User.Read.All` | Read all user profiles | Resolving DDG membership, reading contact fields |
| `Group.Read.All` | Read all groups | Discovering Dynamic Distribution Groups |
| `GroupMember.Read.All` | Read group memberships | Resolving DDG transitive members |
| `Contacts.ReadWrite` | Read/write contacts in all mailboxes | Creating and updating contact folders and contacts |
| `MailboxSettings.Read` | Read mailbox settings | Checking mailbox availability before sync |

4. Click **Add permissions** after selecting all five

## Step 5: Grant Admin Consent

1. On the **API permissions** page, click **Grant admin consent for [Your Tenant Name]**
2. Click **Yes** to confirm
3. Verify all five permissions show a **green checkmark** in the Status column

> **NOTE:** Admin consent is required because these are Application permissions (not Delegated). Only a Global Administrator or Privileged Role Administrator can grant consent.

## Step 6: Update Configuration

### For Local Development

Update `api/appsettings.Development.json`:

```json
{
  "Graph": {
    "TenantId": "your-tenant-id-here",
    "ClientId": "your-client-id-here",
    "ClientSecret": "your-client-secret-here"
  }
}
```

### For Production (Docker)

Set environment variables in `.env` or `compose.yaml`:

```env
GRAPH__TENANTID=your-tenant-id-here
GRAPH__CLIENTID=your-client-id-here
GRAPH__CLIENTSECRET=your-client-secret-here
```

> **NOTE:** ASP.NET Core uses `__` (double underscore) as the hierarchy separator for environment variables. `GRAPH__TENANTID` maps to `Graph:TenantId` in configuration.

## Step 7: Verify Configuration

After updating the configuration, start the API and test the Graph health endpoint:

```bash
# Start the API
cd api && dotnet run

# Test Graph health (should return 200 with permission details)
curl http://localhost:8080/health/graph
```

### Expected Response (Success)

```json
{
  "isHealthy": true,
  "message": "Graph connection verified",
  "tenantName": "Atlanta Fine Homes Sotheby's International Realty",
  "permissions": [
    "User.Read.All (verified - user query succeeded)",
    "Group.Read.All (not tested - requires Phase 2+)",
    "GroupMember.Read.All (not tested - requires Phase 2+)",
    "Contacts.ReadWrite (not tested - requires Phase 2+)",
    "MailboxSettings.Read (not tested - requires Phase 2+)"
  ]
}
```

### Expected Response (Not Configured)

```json
{
  "isHealthy": false,
  "message": "Graph credentials not configured",
  "permissions": []
}
```

### Expected Response (Invalid Credentials)

```json
{
  "isHealthy": false,
  "message": "Graph connection failed: <error details>",
  "permissions": []
}
```

## Troubleshooting

### "Insufficient privileges" Error

- Verify admin consent was granted (Step 5)
- Wait up to 5 minutes for permissions to propagate
- Check that the correct permissions were added (Application, not Delegated)

### "AADSTS7000215: Invalid client secret"

- The client secret may have expired or been entered incorrectly
- Create a new client secret (Step 3) and update the configuration

### "AADSTS700016: Application not found"

- Verify the Client ID is correct
- Ensure the app registration is in the correct tenant

### Permissions Not Propagating

- New app registrations and permission grants can take 1-3 days to fully propagate in some edge cases
- Most propagation completes within 5-15 minutes
- If permissions still fail after 30 minutes, try removing and re-granting admin consent

## Security Notes

- **Never commit secrets to source control.** Use environment variables or Azure Key Vault.
- **Rotate the client secret** before it expires (24-month default).
- **Monitor sign-in logs** in Entra ID > Enterprise applications > AFH Sync > Sign-in logs to track API usage.
- The app uses **Application permissions** (not Delegated) -- it operates without user context, which is required for daemon/background sync operations.
