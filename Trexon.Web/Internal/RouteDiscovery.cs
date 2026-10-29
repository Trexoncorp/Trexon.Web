using System.Reflection;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Routing;

namespace Trexon.Web.Internal;

internal sealed class RouteFeatureProvider(HashSet<Type> routes) : IApplicationFeatureProvider<ControllerFeature>
{
    public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
    {
        feature.Controllers.Clear();
        foreach (var route in routes) feature.Controllers.Add(route.GetTypeInfo());
    }
}
internal sealed class ExplicitActionsConvention : IApplicationModelConvention
{
    public void Apply(ApplicationModel application)
    {
        foreach (var controller in application.Controllers)
            for (var i = controller.Actions.Count - 1; i >= 0; i--)
                if (!controller.Actions[i].Attributes.OfType<HttpMethodAttribute>().Any())
                    controller.Actions.RemoveAt(i);
    }
}
