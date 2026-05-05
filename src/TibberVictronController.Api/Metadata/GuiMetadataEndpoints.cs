namespace TibberVictronController.Api.Metadata;

public static class GuiMetadataEndpoints
{
    public static IEndpointRouteBuilder MapGuiMetadataEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
            "/api/gui/metadata",
            GetGuiMetadata)
            .WithName("GetGuiMetadata")
            .WithTags("Metadata");

        return endpoints;
    }

    public static IResult GetGuiMetadata()
    {
        return TypedResults.Ok(GuiMetadataCatalog.Create());
    }
}
