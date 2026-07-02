# IIS Setup for Interactive Documentation

## Location
The interactive documentation files are now accessible via IIS at:
- **Physical Path**: `c:\CyberProjects\apidocumentation\interactive-test`
- **URL**: `http://localhost/interactive-test` (after IIS configuration)

## IIS Configuration Steps

### Option 1: Virtual Directory (Recommended)
1. Open **Internet Information Services (IIS) Manager**
2. Expand the server node and locate your web site (likely "Default Web Site")
3. Right-click the web site → **Add Virtual Directory**
   - **Alias**: `interactive-test`
   - **Physical path**: `c:\CyberProjects\apidocumentation\interactive-test`
4. Click **OK**
5. Select the newly created `interactive-test` folder
6. Double-click **Handler Mappings** → verify static files are enabled
7. Test: Browse to `http://localhost/interactive-test/DocumentationLogin.html`

### Option 2: Application Pool (More Isolation)
1. Open **Internet Information Services (IIS) Manager**
2. Right-click **Application Pools** → **Add Application Pool**
   - **Name**: `InteractiveTestPool`
   - **.NET Framework version**: No Managed Code (for static files)
   - Click **OK**
3. Right-click the web site → **Add Application**
   - **Alias**: `interactive-test`
   - **Application pool**: `InteractiveTestPool`
   - **Physical path**: `c:\CyberProjects\apidocumentation\interactive-test`
4. Click **OK**
5. Test: Browse to `http://localhost/interactive-test/DocumentationLogin.html`

## Important Files

| File | Purpose |
|------|---------|
| `DocumentationLogin.html` | Entry point - login with IntegrationApiKey |
| `index.html` | Interactive API documentation (after login) |
| `SupplierAdminLogin.html` | Admin authentication |
| `SupplierAdminPanel.html` | Manage hubs and endpoint visibility |
| `SupplierServiceVisibility.html` | Configure which endpoints are visible per hub |
| `app.js` | Main application logic |
| `styles.css` | Styling |
| `web.config` | IIS configuration (static files, MIME types, security headers) |

## URL Routing

After IIS setup, access the application at:

- **Documentation (User)**: `http://your-domain/interactive-test/DocumentationLogin.html`
- **Admin Panel**: `http://your-domain/interactive-test/SupplierAdminLogin.html`
- **Direct API Docs** (requires login): `http://your-domain/interactive-test/index.html`

## Session Storage

The application uses:
- **sessionStorage**: Short-lived session data (cleared on browser close)
  - `supplier_docs_session` - User login session
  - `supplier_admin_key` - Admin authentication
  - `supplier_admin_hubs` - Hub configuration cache

- **localStorage**: Persistent data (survives browser restart)
  - `supplier_admin_hubs` - Hub configuration
  - `supplier_endpoint_visibility::{hubId}` - Visibility rules per hub
  - `supplierapi_custom_base` - Last used base URL
  - `supplierapi_custom_key` - Last used API key

## CORS & API Proxy

The application makes requests to the SupplierAPI base URL specified in DocumentationLogin. If the API is on a different domain, ensure:

1. The API supports CORS headers, or
2. Use a proxy (the included `interactive-proxy-server.py` provides this locally)

## Troubleshooting

### "404 Not Found" errors for static files
- Check Handler Mappings - ensure StaticFile handler is enabled
- Verify MIME types in web.config are correct
- Check file permissions - IIS AppPool identity needs read access

### Blank page or "Loading..." stuck
- Check browser console (F12) for JavaScript errors
- Verify API base URL is accessible
- Check browser localStorage/sessionStorage for corruption

### Admin Panel shows "No hubs configured"
- Sign in to Admin Panel via `SupplierAdminLogin.html`
- Create at least one hub with:
  - Hub Name
  - Base URL (e.g., `http://api.example.com`)
  - Encoded API Key
- The hub data is persisted in localStorage and sessionStorage

### Session clears after page reload
- This is by design - user sessions use sessionStorage only
- To persist across browser closes, user must stay logged in within a tab
- Use "Reset Session" button to clear stale sessions and re-login

## Performance Tuning (Optional)

Enable Output Caching in IIS for static files:
1. Select the `interactive-test` folder in IIS Manager
2. Double-click **Output Caching**
3. Right-click → **Add...**
   - **File name extension**: `.js`, `.css`, `.png`, `.svg`, etc.
   - **User-specified headers**: Keep empty
4. Set cache duration (e.g., 1 day for assets, 5 min for HTML)

This can be configured in web.config using `<caching>` rules.

## Additional Notes

- The application requires **no backend database** - all hub configuration is stored in client-side storage
- The Python proxy server (`interactive-proxy-server.py`) is not needed when using IIS; IIS will serve static files directly
- CORS requests from the documentation to the SupplierAPI backend should work if the API is properly configured or if behind an application gateway that handles CORS

---

**Status**: ✓ Files copied to IIS-accessible location  
**Next**: Configure virtual directory in IIS Manager per steps above
