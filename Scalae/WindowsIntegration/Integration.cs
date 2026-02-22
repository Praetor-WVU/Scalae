using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Scalae.Logging;
using Scalae.Models;
using System;
using System.DirectoryServices;
using System.DirectoryServices.AccountManagement;
using System.Net;
using System.Security.Principal;

namespace Scalae.WindowsIntegration
{
    /// <summary>
    /// Represents a Windows Domain integration. Instances store domain connection info and provide
    /// helper methods to validate credentials and determine whether a user/machine is allowed.
    /// </summary>
    internal class Integration
    {
        private readonly ILogger<Integration> _logger;

        // Accept the logging abstraction via constructor injection.
        public Integration(ILoggingService? loggingService = null)
        {
            _logger = loggingService?.CreateLogger<Integration>() ?? NullLogger<Integration>.Instance;
        }
        public string DomainName { get; }
        /// Optional domain controller or LDAP path (can be null to let API locate DCs).
        public string? DomainController { get; }

        // Optional service account credentials used for some queries. Avoid storing long-lived plaintext in production.
        private readonly string? _serviceAccountUser;
        private readonly string? _serviceAccountPassword;

        public Integration(string domainName, string? domainController = null, string? serviceAccountUser = null, string? serviceAccountPassword = null)
        {
            if (string.IsNullOrWhiteSpace(domainName)) throw new ArgumentNullException(nameof(domainName));
            DomainName = domainName;
            DomainController = domainController;
            _serviceAccountUser = serviceAccountUser;
            _serviceAccountPassword = serviceAccountPassword;
        }

        private PrincipalContext CreateContext()
        {
            if (!string.IsNullOrEmpty(_serviceAccountUser) && _serviceAccountPassword != null)
            {
                _logger.LogDebug("Creating PrincipalContext with service account credentials for domain {Domain}", DomainName);
                return new PrincipalContext(ContextType.Domain, DomainController ?? DomainName, _serviceAccountUser, _serviceAccountPassword);
            }
            _logger.LogDebug("Creating PrincipalContext without explicit credentials for domain {Domain}", DomainName);
            return new PrincipalContext(ContextType.Domain, DomainController ?? DomainName);
        }

        /// <summary>
        /// Validates credentials against the configured domain.
        /// </summary>
        public bool ValidateCredentials(string username, string password)
        {
            if (string.IsNullOrWhiteSpace(username)) throw new ArgumentNullException(nameof(username));
            if (password == null) throw new ArgumentNullException(nameof(password));

            try
            {
                using var ctx = new PrincipalContext(ContextType.Domain, DomainController ?? DomainName);
                return ctx.ValidateCredentials(username, password, ContextOptions.Negotiate);
            }
            catch
            {
                _logger.LogWarning("Credential validation failed for user {User} in domain {Domain}", username, DomainName);
                return false;
            }
        }

        /// <summary>
        /// Returns true if the given domain user is a domain administrator (best-effort).
        /// </summary>
        public bool IsUserDomainAdmin(string samAccountName)
        {
            if (string.IsNullOrWhiteSpace(samAccountName)) throw new ArgumentNullException(nameof(samAccountName));

            try
            {
                using var ctx = CreateContext();
                var user = UserPrincipal.FindByIdentity(ctx, IdentityType.SamAccountName, samAccountName)
                           ?? UserPrincipal.FindByIdentity(ctx, IdentityType.Name, samAccountName)
                           ?? UserPrincipal.FindByIdentity(ctx, IdentityType.UserPrincipalName, samAccountName);

                if (user == null) return false;

                // Check membership in common admin groups
                var domainAdmins = GroupPrincipal.FindByIdentity(ctx, "Domain Admins");
                if (domainAdmins != null && user.IsMemberOf(domainAdmins)) return true;

                var enterpriseAdmins = GroupPrincipal.FindByIdentity(ctx, "Enterprise Admins");
                if (enterpriseAdmins != null && user.IsMemberOf(enterpriseAdmins)) return true;

                var localAdmins = GroupPrincipal.FindByIdentity(ctx, "Administrators");
                if (localAdmins != null && user.IsMemberOf(localAdmins)) return true;

                _logger.LogInformation("User {User} is not a domain admin in domain {Domain}", samAccountName, DomainName);
                return false;
            }
            catch
            {
                _logger.LogWarning("Failed to check domain admin status for user {User} in domain {Domain}", samAccountName, DomainName);
                return false;
            }
        }

        /// <summary>
        /// Best-effort check whether the given machine appears to belong to this domain.
        /// Uses reverse DNS (FQDN) and the machine's reported name if available.
        /// </summary>
        public bool IsMachineInDomain(Models.ClientMachine machine)
        {
            if (machine == null) throw new ArgumentNullException(nameof(machine));

            // If machine has an FQDN-like name, check suffix
            if (!string.IsNullOrWhiteSpace(machine.Name))
            {
                var nm = machine.Name.Trim();
                if (nm.Contains(".") && nm.EndsWith(DomainName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            // Try reverse DNS on IP if available
            if (!string.IsNullOrWhiteSpace(machine.IPAddress))
            {
                try
                {
                    var entry = Dns.GetHostEntry(machine.IPAddress);
                    if (!string.IsNullOrWhiteSpace(entry.HostName) && entry.HostName.EndsWith(DomainName, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch
                {
                    _logger.LogWarning("Reverse DNS lookup failed for IP {IP} when checking domain membership for machine {Machine}", machine.IPAddress, machine.Name);
                    // ignore DNS failures
                }
            }
            _logger.LogInformation("Unable to determine domain membership for machine {Machine} (Name: {Name}, IP: {IP}) in domain {Domain}", machine.Name, machine.Name, machine.IPAddress, DomainName);
            // Unable to determine domain membership -> treat as not in domain
            return false;
        }

        /// <summary>
        /// Determines whether access to the machine should be allowed under this integration.
        /// Rules:
        /// - Machine must be in the same domain as this integration
        /// - User must be a domain admin in that domain (checked by samAccountName or current Windows identity if null)
        /// </summary>
        public bool AllowsAccess(Models.ClientMachine machine, string? samAccountName = null, string? passwordIfNeeded = null)
        {
            if (machine == null) throw new ArgumentNullException(nameof(machine));

            if (!IsMachineInDomain(machine))
                return false; // disallow access to machines not in integrated domain

            // Determine user to check
            string? checkUser = samAccountName;
            if (string.IsNullOrEmpty(checkUser))
            {
                // Use current Windows identity (format DOMAIN\user or user@domain)
                try
                {
                    var win = WindowsIdentity.GetCurrent();
                    if (win != null)
                    {
                        checkUser = win.Name; // may be DOMAIN\user
                        // normalize to samAccountName if possible (strip domain\)
                        if (checkUser.Contains("\\"))
                            checkUser = checkUser.Substring(checkUser.IndexOf('\\') + 1);
                    }
                }
                catch { /* ignore */ }
            }

            if (string.IsNullOrEmpty(checkUser))
                return false;

            // If caller supplied credentials, prefer validating them first
            if (!string.IsNullOrEmpty(passwordIfNeeded))
            {
                if (!ValidateCredentials(checkUser, passwordIfNeeded))
                    return false;
            }

            // Finally check admin membership
            return IsUserDomainAdmin(checkUser);
        }

        public override string ToString() => $"Integration[Domain={DomainName}, DC={DomainController ?? "(auto)"}]";
    }
}
