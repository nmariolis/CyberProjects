using System;
using System.Configuration;
using System.Net.Mail;

namespace Documentation.Services
{
    // Thin wrapper around System.Net.Mail.SmtpClient. SMTP host / port / credentials
    // come from the standard .NET <system.net><mailSettings><smtp> section in
    // web.config — see the commented sample there for the expected shape.
    //
    // Two appSettings drive behaviour:
    //   • EmailEnabled   — "true" turns sending on. Anything else (or missing) keeps
    //                      sending off and TrySend returns false. Useful so a dev
    //                      install doesn't try to spray real mail.
    //   • EmailFromAddress — fallback "from" address if mailSettings doesn't supply one.
    //
    // The API never throws on send failure: callers inspect the bool. Admin endpoints
    // still return the generated temp password in the JSON body when email fails so
    // the operator can hand it over manually.
    public static class EmailService
    {
        public static bool IsConfigured()
        {
            var enabled = string.Equals(
                ConfigurationManager.AppSettings["EmailEnabled"] ?? string.Empty,
                "true", StringComparison.OrdinalIgnoreCase);
            return enabled;
        }

        public static bool TrySend(string to, string subject, string body, out string error)
        {
            error = null;
            if (!IsConfigured()) { error = "email-disabled"; return false; }
            if (string.IsNullOrWhiteSpace(to)) { error = "empty-recipient"; return false; }

            try
            {
                using (var msg = new MailMessage())
                using (var client = new SmtpClient())  // picks up <mailSettings><smtp>
                {
                    var from = ConfigurationManager.AppSettings["EmailFromAddress"];
                    if (!string.IsNullOrWhiteSpace(from))
                        msg.From = new MailAddress(from);
                    // else: SmtpClient/MailMessage will use the <smtp from="..."> attribute.

                    msg.To.Add(to.Trim());
                    msg.Subject    = subject ?? string.Empty;
                    msg.Body       = body ?? string.Empty;
                    msg.IsBodyHtml = false;
                    client.Send(msg);
                    return true;
                }
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        // Convenience builder for the credential-handover email.
        public static string BuildCredentialBody(string email, string tempPassword, string portalUrl)
        {
            return
                "Hello,\r\n\r\n" +
                "An account has been provisioned for you on the Cyberlogic API documentation admin panel.\r\n\r\n" +
                "  Email:            " + email + "\r\n" +
                "  Temporary password: " + tempPassword + "\r\n\r\n" +
                "Please sign in and change your password immediately. You will be prompted to do so on first login.\r\n\r\n" +
                (string.IsNullOrWhiteSpace(portalUrl) ? string.Empty : ("Portal: " + portalUrl + "\r\n\r\n")) +
                "If you did not expect this email, please ignore it.\r\n\r\n" +
                "— Cyberlogic e-Tourism Platform";
        }
    }
}
