using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Quicker.Audit.Contracts;
using Quicker.Collaboration.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;
using Quicker.Kernel.Time;
using Quicker.Organization.Contracts;
using Quicker.Partners.Contracts;
using Quicker.Partners.Domain;
using Quicker.Partners.Persistence;
using Quicker.Web;

namespace Quicker.Partners.Application;

/// <summary>
/// The partner record and what hangs off it: contacts, addresses, bank accounts (numbers encrypted under the platform
/// key, revealed only with a permission and an audit event) and tax registrations.
/// </summary>
public sealed class PartnerService(
    PartnersDbContext db,
    ICompanyDirectory companies,
    ICustomFieldValidator customFields,
    ISecretProtector secrets,
    ICurrentPrincipal principal,
    IAuditSink audit,
    IClock clock,
    SupplierService suppliers)
{
    public const string EntityType = "partner";

    private static readonly IReadOnlyList<string> AddressRoles = ["billing", "shipping", "legal", "other"];

    private static readonly IReadOnlyList<string> RegistrationTypes = ["vat", "tin", "crn", "other"];

    public async Task<Result<Page<PartnerSummary>>> ListAsync(string? q, string? role, bool? isActive, PageRequest page, CancellationToken cancellationToken)
    {
        var query = db.Partners.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var pattern = "%" + q.Trim() + "%";
            query = db.Partners.FromSqlInterpolated($"SELECT * FROM app.ptr_partners WHERE code ILIKE {pattern} OR legal_name_i18n->>'en' ILIKE {pattern} OR legal_name_i18n->>'ar' ILIKE {pattern} OR trade_name_i18n->>'en' ILIKE {pattern} OR trade_name_i18n->>'ar' ILIKE {pattern} OR email ILIKE {pattern}").AsNoTracking();
        }

        switch (role?.Trim().ToLowerInvariant())
        {
            case "supplier":
                query = query.Where(static p => p.IsSupplier);
                break;
            case "customer":
                query = query.Where(static p => p.IsCustomer);
                break;
            case "employee":
                query = query.Where(static p => p.IsEmployee);
                break;
            case null or "":
                break;
            default:
                return Error.Validation("partner.role_invalid", "role is supplier, customer or employee.").WithWhy(("role", role));
        }

        if (isActive is { } active)
        {
            query = query.Where(p => p.IsActive == active);
        }

        var paged = await KeysetPaging.ByIdDescendingAsync(query, static p => p.Id, page, cancellationToken);
        if (paged.IsFailure)
        {
            return paged.Error!;
        }

        var ids = paged.Value.Items.Select(static p => p.Id).ToList();
        var parents = await db.Partners.AsNoTracking().Where(p => paged.Value.Items.Select(static x => x.ParentPartnerId).Contains(p.Id)).ToDictionaryAsync(static p => p.Id, static p => p.Code, cancellationToken);
        var supplierCounts = await db.SupplierAccounts.AsNoTracking().Where(a => ids.Contains(a.PartnerId)).GroupBy(static a => a.PartnerId).Select(static g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(static g => g.Key, static g => g.Count, cancellationToken);
        return paged.Value.Map(p => Map(p, p.ParentPartnerId is { } parent ? parents.GetValueOrDefault(parent) : null, supplierCounts.GetValueOrDefault(p.Id)));
    }

    public async Task<PartnerDetail?> GetAsync(Guid partnerId, CancellationToken cancellationToken)
    {
        var partner = await db.Partners.AsNoTracking().SingleOrDefaultAsync(p => p.Id == partnerId, cancellationToken);
        return partner is null ? null : await DetailAsync(partner, cancellationToken);
    }

    public async Task<PartnerDetail?> GetByCodeAsync(string code, CancellationToken cancellationToken)
    {
        var normalized = code.Trim().ToUpperInvariant();
        var partner = await db.Partners.AsNoTracking().SingleOrDefaultAsync(p => p.Code == normalized, cancellationToken);
        return partner is null ? null : await DetailAsync(partner, cancellationToken);
    }

    public async Task<Result<PartnerSummary>> CreateAsync(SavePartnerRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var partner = new Partner { Id = Guid.CreateVersion7(), CreatedAt = clock.UtcNow };
        var applied = await ApplyAsync(partner, request, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        db.Partners.Add(partner);
        await db.SaveChangesAsync(cancellationToken);
        var parentCode = partner.ParentPartnerId is { } parent ? await db.Partners.Where(p => p.Id == parent).Select(static p => p.Code).SingleOrDefaultAsync(cancellationToken) : null;
        return Map(partner, parentCode, 0);
    }

    public async Task<Result<PartnerSummary>> UpdateAsync(Guid partnerId, SavePartnerRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var partner = await db.Partners.SingleOrDefaultAsync(p => p.Id == partnerId, cancellationToken);
        if (partner is null)
        {
            return Error.NotFound(EntityType, partnerId);
        }

        var applied = await ApplyAsync(partner, request, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        if (!partner.IsSupplier && await db.SupplierAccounts.AnyAsync(a => a.PartnerId == partnerId && a.IsActive, cancellationToken))
        {
            return Error.Conflict("partner.supplier_accounts_active", "Deactivate the partner's supplier accounts before removing the supplier role.");
        }

        await db.SaveChangesAsync(cancellationToken);
        var parentCode = partner.ParentPartnerId is { } parent ? await db.Partners.Where(p => p.Id == parent).Select(static p => p.Code).SingleOrDefaultAsync(cancellationToken) : null;
        return Map(partner, parentCode, await db.SupplierAccounts.CountAsync(a => a.PartnerId == partnerId, cancellationToken));
    }

    private async Task<Result> ApplyAsync(Partner partner, SavePartnerRequest request, CancellationToken cancellationToken)
    {
        var code = Validation.Code(request.Code, EntityType);
        if (code.IsFailure)
        {
            return code.Error!;
        }

        if (code.Value != partner.Code && await db.Partners.AnyAsync(p => p.Code == code.Value, cancellationToken))
        {
            return Error.Conflict("partner.code_taken", $"A partner with code '{code.Value}' already exists.").WithWhy(("code", code.Value));
        }

        var legalName = Validation.Name(request.LegalName, EntityType);
        if (legalName.IsFailure)
        {
            return legalName.Error!;
        }

        var kind = Validation.OneOf(request.Kind, "partner.kind", PartnerKinds.All);
        if (kind.IsFailure)
        {
            return kind.Error!;
        }

        var email = Validation.Email(request.Email, EntityType);
        if (email.IsFailure)
        {
            return email.Error!;
        }

        var language = (request.DefaultLanguage ?? "en").Trim().ToLowerInvariant();
        if (language is not ("en" or "ar"))
        {
            return Error.Validation("partner.language_invalid", "The default language is en or ar.").WithWhy(("language", request.DefaultLanguage));
        }

        if (request.IntercompanyCompanyId is { } company && await companies.FindAsync(new CompanyId(company), cancellationToken) is null)
        {
            return Error.Validation("partner.intercompany_company_unknown", "The intercompany company does not exist.").WithWhy(("companyId", company));
        }

        if (request.ParentPartnerId is { } parentId)
        {
            if (parentId == partner.Id)
            {
                return Error.Validation("partner.parent_is_self", "A partner cannot be its own parent.");
            }

            if (!await db.Partners.AnyAsync(p => p.Id == parentId, cancellationToken))
            {
                return Error.Validation("partner.parent_unknown", "The parent partner does not exist.").WithWhy(("parentPartnerId", parentId));
            }
        }

        var validated = await customFields.ValidateAsync(EntityType, request.CustomFields ?? (partner.CreatedAt == default ? null : JsonDocument.Parse(partner.CustomFields).RootElement), cancellationToken);
        if (validated.IsFailure)
        {
            return validated.Error!;
        }

        partner.Code = code.Value;
        partner.LegalName = legalName.Value;
        partner.TradeName = new LocalizedText(request.TradeName ?? new Dictionary<string, string>(StringComparer.Ordinal));
        partner.Kind = kind.Value;
        partner.IsSupplier = request.IsSupplier;
        partner.IsCustomer = request.IsCustomer;
        partner.IsEmployee = request.IsEmployee;
        partner.IntercompanyCompanyId = request.IntercompanyCompanyId;
        partner.DefaultLanguage = language;
        partner.Website = Trim(request.Website);
        partner.Email = email.Value;
        partner.Phone = Trim(request.Phone);
        partner.ParentPartnerId = request.ParentPartnerId;
        partner.Notes = Trim(request.Notes);
        partner.CustomFields = validated.Value;
        partner.IsActive = request.IsActive;
        partner.UpdatedAt = clock.UtcNow;
        return Result.Success();
    }

    // ------------------------------------------------------------------ contacts

    public async Task<Result<ContactSummary>> SaveContactAsync(Guid partnerId, Guid? contactId, SaveContactRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await db.Partners.AnyAsync(p => p.Id == partnerId, cancellationToken))
        {
            return Error.NotFound(EntityType, partnerId);
        }

        var name = Validation.Name(request.Name, "contact");
        if (name.IsFailure)
        {
            return name.Error!;
        }

        var email = Validation.Email(request.Email, "contact");
        if (email.IsFailure)
        {
            return email.Error!;
        }

        Contact? contact = null;
        if (contactId is { } id)
        {
            contact = await db.Contacts.SingleOrDefaultAsync(c => c.Id == id && c.PartnerId == partnerId, cancellationToken);
            if (contact is null)
            {
                return Error.NotFound("contact", id);
            }
        }

        var isNew = contact is null;
        contact ??= new Contact { Id = Guid.CreateVersion7(), PartnerId = partnerId, CreatedAt = clock.UtcNow };
        contact.Name = name.Value;
        contact.Role = Trim(request.Role);
        contact.Email = email.Value;
        contact.Phone = Trim(request.Phone);
        contact.Mobile = Trim(request.Mobile);
        contact.IsPrimary = request.IsPrimary;
        contact.ReceivesStatements = request.ReceivesStatements;
        contact.Notes = Trim(request.Notes);
        contact.IsActive = request.IsActive;
        contact.UpdatedAt = clock.UtcNow;
        if (isNew)
        {
            db.Contacts.Add(contact);
        }

        if (contact.IsPrimary)
        {
            // One primary contact per partner.
            foreach (var other in await db.Contacts.Where(c => c.PartnerId == partnerId && c.Id != contact.Id && c.IsPrimary).ToListAsync(cancellationToken))
            {
                other.IsPrimary = false;
                other.UpdatedAt = clock.UtcNow;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return Map(contact);
    }

    public async Task<Result> DeleteContactAsync(Guid partnerId, Guid contactId, CancellationToken cancellationToken)
    {
        var contact = await db.Contacts.SingleOrDefaultAsync(c => c.Id == contactId && c.PartnerId == partnerId, cancellationToken);
        if (contact is null)
        {
            return Error.NotFound("contact", contactId);
        }

        db.Contacts.Remove(contact);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    // ------------------------------------------------------------------ addresses

    public async Task<Result<AddressSummary>> SaveAddressAsync(Guid partnerId, Guid? addressId, SaveAddressRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await db.Partners.AnyAsync(p => p.Id == partnerId, cancellationToken))
        {
            return Error.NotFound(EntityType, partnerId);
        }

        var role = Validation.OneOf(request.Role, "address.role", AddressRoles);
        if (role.IsFailure)
        {
            return role.Error!;
        }

        var country = Validation.Country(request.Country, "address");
        if (country.IsFailure)
        {
            return country.Error!;
        }

        PartnerAddress? address = null;
        if (addressId is { } id)
        {
            address = await db.Addresses.SingleOrDefaultAsync(a => a.Id == id && a.PartnerId == partnerId, cancellationToken);
            if (address is null)
            {
                return Error.NotFound("address", id);
            }
        }

        var isNew = address is null;
        address ??= new PartnerAddress { Id = Guid.CreateVersion7(), PartnerId = partnerId, CreatedAt = clock.UtcNow };
        address.Role = role.Value;
        address.Address = Validation.JsonOrEmpty(request.Address);
        address.Country = country.Value;
        address.Region = Trim(request.Region);
        address.IsDefault = request.IsDefault;
        address.UpdatedAt = clock.UtcNow;
        if (isNew)
        {
            db.Addresses.Add(address);
        }

        if (address.IsDefault)
        {
            foreach (var other in await db.Addresses.Where(a => a.PartnerId == partnerId && a.Role == address.Role && a.Id != address.Id && a.IsDefault).ToListAsync(cancellationToken))
            {
                other.IsDefault = false;
                other.UpdatedAt = clock.UtcNow;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return Map(address);
    }

    public async Task<Result> DeleteAddressAsync(Guid partnerId, Guid addressId, CancellationToken cancellationToken)
    {
        var address = await db.Addresses.SingleOrDefaultAsync(a => a.Id == addressId && a.PartnerId == partnerId, cancellationToken);
        if (address is null)
        {
            return Error.NotFound("address", addressId);
        }

        db.Addresses.Remove(address);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    // ------------------------------------------------------------------ bank accounts

    public async Task<Result<BankAccountSummary>> SaveBankAccountAsync(Guid partnerId, Guid? accountId, SaveBankAccountRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await db.Partners.AnyAsync(p => p.Id == partnerId, cancellationToken))
        {
            return Error.NotFound(EntityType, partnerId);
        }

        if (string.IsNullOrWhiteSpace(request.BankName))
        {
            return Error.Validation("bank_account.bank_required", "The bank's name is required.");
        }

        var currencyCode = (request.Currency ?? string.Empty).Trim().ToUpperInvariant();
        if (await companies.FindCurrencyAsync(currencyCode, cancellationToken) is null)
        {
            return Error.Validation("bank_account.currency_unknown", "The currency is not an ISO 4217 code the system knows.").WithWhy(("currency", request.Currency));
        }

        PartnerBankAccount? account = null;
        if (accountId is { } id)
        {
            account = await db.BankAccounts.SingleOrDefaultAsync(a => a.Id == id && a.PartnerId == partnerId, cancellationToken);
            if (account is null)
            {
                return Error.NotFound("bank_account", id);
            }
        }

        var isNew = account is null;
        account ??= new PartnerBankAccount { Id = Guid.CreateVersion7(), PartnerId = partnerId, CreatedAt = clock.UtcNow };
        if (request.Iban is not null)
        {
            var iban = Validation.Iban(request.Iban);
            if (iban.IsFailure)
            {
                return iban.Error!;
            }

            account.IbanEnc = secrets.ProtectString(iban.Value);
            account.IbanMasked = Validation.Mask(iban.Value);
        }

        if (request.AccountNumber is not null)
        {
            var number = Validation.AccountNumber(request.AccountNumber);
            if (number.IsFailure)
            {
                return number.Error!;
            }

            account.AccountNumberEnc = secrets.ProtectString(number.Value);
            account.AccountNumberMasked = Validation.Mask(number.Value);
        }

        if (account.IbanEnc is null && account.AccountNumberEnc is null)
        {
            return Error.Validation("bank_account.identifier_required", "An account number or an IBAN is required.");
        }

        account.AccountHolder = Trim(request.AccountHolder);
        account.BankName = request.BankName.Trim();
        account.Branch = Trim(request.Branch);
        account.SwiftBic = Trim(request.SwiftBic)?.ToUpperInvariant();
        account.Currency = currencyCode;
        account.IsDefault = request.IsDefault;
        account.IsActive = request.IsActive;
        account.UpdatedAt = clock.UtcNow;
        if (isNew)
        {
            db.BankAccounts.Add(account);
        }

        if (account.IsDefault)
        {
            foreach (var other in await db.BankAccounts.Where(a => a.PartnerId == partnerId && a.Id != account.Id && a.IsDefault).ToListAsync(cancellationToken))
            {
                other.IsDefault = false;
                other.UpdatedAt = clock.UtcNow;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return Map(account);
    }

    public async Task<Result> DeleteBankAccountAsync(Guid partnerId, Guid accountId, CancellationToken cancellationToken)
    {
        var account = await db.BankAccounts.SingleOrDefaultAsync(a => a.Id == accountId && a.PartnerId == partnerId, cancellationToken);
        if (account is null)
        {
            return Error.NotFound("bank_account", accountId);
        }

        db.BankAccounts.Remove(account);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    /// <summary>The full identifiers, decrypted for this one response and recorded in the audit trail (who, when, which account).</summary>
    public async Task<Result<BankAccountReveal>> RevealBankAccountAsync(Guid partnerId, Guid accountId, CancellationToken cancellationToken)
    {
        var account = await db.BankAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == accountId && a.PartnerId == partnerId, cancellationToken);
        if (account is null)
        {
            return Error.NotFound("bank_account", accountId);
        }

        await audit.RecordAsync(new AuditEntry("partner_bank_account", account.Id, account.BankName + " " + (account.IbanMasked ?? account.AccountNumberMasked), "revealed", After: new { partnerId, revealedBy = principal.Principal?.UserId.Value, iban = account.IbanMasked, accountNumber = account.AccountNumberMasked }), cancellationToken);
        return new BankAccountReveal(account.Id, account.AccountNumberEnc is null ? null : secrets.UnprotectString(account.AccountNumberEnc), account.IbanEnc is null ? null : secrets.UnprotectString(account.IbanEnc));
    }

    // ------------------------------------------------------------------ tax registrations

    public async Task<Result<TaxRegistrationSummary>> SaveTaxRegistrationAsync(Guid partnerId, Guid? registrationId, SaveTaxRegistrationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await db.Partners.AnyAsync(p => p.Id == partnerId, cancellationToken))
        {
            return Error.NotFound(EntityType, partnerId);
        }

        var country = Validation.Country(request.Country, "tax_registration");
        if (country.IsFailure)
        {
            return country.Error!;
        }

        var type = Validation.OneOf(request.RegistrationType, "tax_registration.type", RegistrationTypes);
        if (type.IsFailure)
        {
            return type.Error!;
        }

        var number = new string((request.Number ?? string.Empty).Where(static c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();
        if (number.Length is < 2 or > 40)
        {
            return Error.Validation("tax_registration.number_invalid", "A registration number is 2–40 characters.");
        }

        if (request.ValidTo is { } to && request.ValidFrom is { } from && to < from)
        {
            return Error.Validation("tax_registration.period_invalid", "The registration ends on or after the day it starts.");
        }

        if (await db.TaxRegistrations.AnyAsync(r => r.PartnerId == partnerId && r.Country == country.Value && r.RegistrationType == type.Value && r.Number == number && r.Id != registrationId, cancellationToken))
        {
            return Error.Conflict("tax_registration.duplicate", "The partner already carries this registration.").WithWhy(("country", country.Value), ("type", type.Value), ("number", number));
        }

        PartnerTaxRegistration? registration = null;
        if (registrationId is { } id)
        {
            registration = await db.TaxRegistrations.SingleOrDefaultAsync(r => r.Id == id && r.PartnerId == partnerId, cancellationToken);
            if (registration is null)
            {
                return Error.NotFound("tax_registration", id);
            }
        }

        var isNew = registration is null;
        registration ??= new PartnerTaxRegistration { Id = Guid.CreateVersion7(), PartnerId = partnerId, CreatedAt = clock.UtcNow };
        registration.Country = country.Value;
        registration.RegistrationType = type.Value;
        registration.Number = number;
        registration.ValidFrom = request.ValidFrom;
        registration.ValidTo = request.ValidTo;
        registration.UpdatedAt = clock.UtcNow;
        if (isNew)
        {
            db.TaxRegistrations.Add(registration);
        }

        await db.SaveChangesAsync(cancellationToken);
        return Map(registration);
    }

    public async Task<Result> DeleteTaxRegistrationAsync(Guid partnerId, Guid registrationId, CancellationToken cancellationToken)
    {
        var registration = await db.TaxRegistrations.SingleOrDefaultAsync(r => r.Id == registrationId && r.PartnerId == partnerId, cancellationToken);
        if (registration is null)
        {
            return Error.NotFound("tax_registration", registrationId);
        }

        db.TaxRegistrations.Remove(registration);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    // ------------------------------------------------------------------ mapping

    private async Task<PartnerDetail> DetailAsync(Partner partner, CancellationToken cancellationToken)
    {
        var parentCode = partner.ParentPartnerId is { } parent ? await db.Partners.AsNoTracking().Where(p => p.Id == parent).Select(static p => p.Code).SingleOrDefaultAsync(cancellationToken) : null;
        var contacts = await db.Contacts.AsNoTracking().Where(c => c.PartnerId == partner.Id).OrderByDescending(static c => c.IsPrimary).ThenBy(static c => c.CreatedAt).ToListAsync(cancellationToken);
        var addresses = await db.Addresses.AsNoTracking().Where(a => a.PartnerId == partner.Id).OrderBy(static a => a.Role).ThenByDescending(static a => a.IsDefault).ToListAsync(cancellationToken);
        var bankAccounts = await db.BankAccounts.AsNoTracking().Where(a => a.PartnerId == partner.Id).OrderByDescending(static a => a.IsDefault).ThenBy(static a => a.CreatedAt).ToListAsync(cancellationToken);
        var registrations = await db.TaxRegistrations.AsNoTracking().Where(r => r.PartnerId == partner.Id).OrderBy(static r => r.Country).ThenBy(static r => r.RegistrationType).ToListAsync(cancellationToken);
        var accounts = await suppliers.ListAccountsOfPartnerAsync(partner.Id, cancellationToken);
        return new PartnerDetail(Map(partner, parentCode, accounts.Count), contacts.Select(Map).ToList(), addresses.Select(Map).ToList(), bankAccounts.Select(Map).ToList(), registrations.Select(Map).ToList(), accounts);
    }

    private static PartnerSummary Map(Partner p, string? parentCode, int supplierCompanies) => new(
        p.Id, p.Code, p.LegalName.Values, p.TradeName.Values, p.Kind, p.IsSupplier, p.IsCustomer, p.IsEmployee, p.IntercompanyCompanyId, p.DefaultLanguage, p.Website, p.Email, p.Phone,
        p.ParentPartnerId, parentCode, p.Notes, JsonDocument.Parse(p.CustomFields).RootElement.Clone(), p.IsActive, supplierCompanies, p.UpdatedAt);

    private static ContactSummary Map(Contact c) => new(c.Id, c.PartnerId, c.Name.Values, c.Role, c.Email, c.Phone, c.Mobile, c.IsPrimary, c.ReceivesStatements, c.Notes, c.IsActive, c.UpdatedAt);

    private static AddressSummary Map(PartnerAddress a) => new(a.Id, a.PartnerId, a.Role, JsonDocument.Parse(a.Address).RootElement.Clone(), a.Country, a.Region, a.IsDefault, a.UpdatedAt);

    private static BankAccountSummary Map(PartnerBankAccount a) => new(a.Id, a.PartnerId, a.AccountHolder, a.BankName, a.Branch, a.SwiftBic, a.Currency, a.AccountNumberMasked, a.IbanMasked, a.IsDefault, a.IsActive, a.UpdatedAt);

    private static TaxRegistrationSummary Map(PartnerTaxRegistration r) => new(r.Id, r.PartnerId, r.Country, r.RegistrationType, r.Number, r.ValidFrom, r.ValidTo, r.UpdatedAt);

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
