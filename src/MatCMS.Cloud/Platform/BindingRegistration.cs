using MatCMS.Cloud.Services;
using MatCMS.Shared;
using MatPlatform;

namespace MatCMS.Cloud.Platform;

/// <summary>
/// What the cloud does for the consumers of <see cref="CloudPlatform.CmsCloud"/>: <c>POST /api/provides/cms.cloud/register</c>, the join of a
/// MatCMS site that a broker (MatOS) bound to this cloud. The key of the binding (<c>Authorization: Bearer</c>, checked by MatPlatform's
/// <see cref="MatBindingAccess"/>) takes the place of the join code; the body and the answer are those of <c>/api/instances/register</c>, so
/// everything after it (heartbeats, sync, updates) is the ordinary instance protocol with the instance token the answer carries.
/// </summary>
public static class BindingRegistration
{
    public static void MapCmsCloudProvider(this WebApplication app)
    {
        var group = app.MapGroup("/api/provides/" + CloudPlatform.CmsCloud)
            .AllowAnonymous()
            .RequireRateLimiting("instanceApi")
            .AddEndpointFilter(MatBindingAccess.Require(CloudPlatform.CmsCloud));

        group.MapPost("/register", async (HttpContext ctx, RegisterRequest request, InstanceService instances) =>
        {
            var consumer = MatBindingAccess.ConsumerOf(ctx) ?? "app";
            var result = await instances.RegisterBoundAsync(request, consumer, ctx.RequestAborted);
            if (result.Instance is null || result.Token is null)
                return Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status409Conflict);

            return Results.Ok(new RegisterResponse
            {
                InstanceId = result.Instance.PublicId,
                Token = result.Token,
                Status = result.Instance.Status.ToString(),
                ProfileName = result.Instance.Profile?.Name,
                DisplayName = result.Instance.Name
            });
        });
    }
}
