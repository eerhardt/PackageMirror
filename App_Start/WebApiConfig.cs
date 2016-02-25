using System;
using System.Net.Http.Formatting;
using System.Net.Http.Headers;
using System.Web.Http;
using PackageMirror.Models;

namespace PackageMirror
{
    public static class WebApiConfig
    {
        public static void Register(HttpConfiguration config)
        {
            // Web API configuration and services
            config.Formatters.Add(
                new TypedJsonMediaTypeFormatter(
                    typeof(WebHookEvent),
                    new MediaTypeHeaderValue("application/vnd.myget.webhooks.v1+json")));

            // Web API routes
            config.MapHttpAttributeRoutes();

            config.Routes.MapHttpRoute(
                name: "DefaultApi",
                routeTemplate: "{controller}/{id}",
                defaults: new { id = RouteParameter.Optional }
            );
        }
    }

    public class TypedJsonMediaTypeFormatter : JsonMediaTypeFormatter
    {
        private readonly Type resourceType;

        public TypedJsonMediaTypeFormatter(Type resourceType, MediaTypeHeaderValue mediaType)
        {
            this.resourceType = resourceType;

            this.SupportedMediaTypes.Clear();
            this.SupportedMediaTypes.Add(mediaType);
        }

        public override bool CanReadType(Type type)
        {
            return this.resourceType == type;
        }

        public override bool CanWriteType(Type type)
        {
            return this.resourceType == type;
        }
    }
}
