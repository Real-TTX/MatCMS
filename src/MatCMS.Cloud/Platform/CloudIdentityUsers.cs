using MatCMS.Cloud.Data;
using MatCMS.Cloud.Services;
using MatPlatform;

namespace MatCMS.Cloud.Platform;

/// <summary>
/// The cloud's side of the sign-in through MatIdentity (MatPlatform <see cref="IMatIdentityUsers"/>): the person becomes a cloud account by
/// <see cref="IdentityAccounts"/> and is signed in with the cloud's own cookie, which keeps MatIdentity's subject, <c>sid</c> and ID token.
/// An account with the same address is not entered by the address: <see cref="MatIdentitySignInResult.LinkRequired"/>, and <c>/login</c>
/// links it after its password (<see cref="MatIdentityPendingLink"/>). The cloud's own second factor is not asked after MatIdentity (whose
/// second step it is). When MatIdentity signs the person out (back-channel logout), the cookies here end too.
/// </summary>
public sealed class CloudIdentityUsers(AppDbContext db, AuthService auth, ILogger<CloudIdentityUsers> logger) : IMatIdentityUsers, IMatIdentitySessions
{
    public async Task<MatIdentitySignInResult> SignInAsync(MatIdentityPerson person, HttpContext http, CancellationToken ct)
    {
        var outcome = await IdentityAccounts.ResolveAsync(db,
            new IdentityAccounts.Person(person.Subject, person.Email, person.Name, person.PreferredUserName, person.Role), auth.HashPassword, ct);
        switch (outcome.Kind)
        {
            case IdentityAccounts.Kind.Link:
                return MatIdentitySignInResult.LinkRequired(outcome.User!.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            case IdentityAccounts.Kind.Refused:
                logger.LogWarning("MatIdentity sign-in of {Subject} refused: {Error}", person.Subject, outcome.Error);
                return MatIdentitySignInResult.Refused(outcome.Error ?? "refused");
        }

        var user = outcome.User!;
        await auth.SignInAsync(http, user, persistent: true, new AuthService.IdentitySession(person.Subject, person.SessionId, person.IdToken));
        logger.LogInformation("User {UserId} signed in through MatIdentity", user.Id);
        return MatIdentitySignInResult.Success();
    }

    public async Task EndSessionsAsync(string? subject, string? sessionId, CancellationToken ct)
    {
        var count = await auth.EndIdentitySessionsAsync(subject, sessionId, ct);
        logger.LogInformation("MatIdentity signed out {Subject} (sid {SessionId}); accounts signed out by subject: {Count}", subject, sessionId, count);
    }
}
