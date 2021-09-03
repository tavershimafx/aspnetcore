// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Threading;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Internal;

namespace Microsoft.AspNetCore.Mvc.ApiExplorer
{
    internal class EndpointMetadataApiDescriptionProvider : IApiDescriptionProvider
    {
        private readonly EndpointDataSource _endpointDataSource;
        private readonly IHostEnvironment _environment;
        private readonly IServiceProviderIsService? _serviceProviderIsService;
        private readonly TryParseMethodCache TryParseMethodCache = new();

        // Executes before MVC's DefaultApiDescriptionProvider and GrpcHttpApiDescriptionProvider for no particular reason.
        public int Order => -1100;

        public EndpointMetadataApiDescriptionProvider(EndpointDataSource endpointDataSource, IHostEnvironment environment)
            : this(endpointDataSource, environment, null)
        {
        }

        public EndpointMetadataApiDescriptionProvider(
            EndpointDataSource endpointDataSource,
            IHostEnvironment environment,
            IServiceProviderIsService? serviceProviderIsService)
        {
            _endpointDataSource = endpointDataSource;
            _environment = environment;
            _serviceProviderIsService = serviceProviderIsService;
        }

        public void OnProvidersExecuting(ApiDescriptionProviderContext context)
        {
            foreach (var endpoint in _endpointDataSource.Endpoints)
            {
                if (endpoint is RouteEndpoint routeEndpoint &&
                    routeEndpoint.Metadata.GetMetadata<MethodInfo>() is { } methodInfo &&
                    routeEndpoint.Metadata.GetMetadata<IHttpMethodMetadata>() is { } httpMethodMetadata &&
                    routeEndpoint.Metadata.GetMetadata<IExcludeFromDescriptionMetadata>() is null or { ExcludeFromDescription: false })
                {
                    // REVIEW: Should we add an ApiDescription for endpoints without IHttpMethodMetadata? Swagger doesn't handle
                    // a null HttpMethod even though it's nullable on ApiDescription, so we'd need to define "default" HTTP methods.
                    // In practice, the Delegate will be called for any HTTP method if there is no IHttpMethodMetadata.
                    foreach (var httpMethod in httpMethodMetadata.HttpMethods)
                    {
                        context.Results.Add(CreateApiDescription(routeEndpoint, httpMethod, methodInfo));
                    }
                }
            }
        }

        public void OnProvidersExecuted(ApiDescriptionProviderContext context)
        {
        }

        private ApiDescription CreateApiDescription(RouteEndpoint routeEndpoint, string httpMethod, MethodInfo methodInfo)
        {
            // Swashbuckle uses the "controller" name to group endpoints together.
            // For now, put all methods defined the same declaring type together.
            string controllerName;

            if (methodInfo.DeclaringType is not null && !TypeHelper.IsCompilerGeneratedType(methodInfo.DeclaringType))
            {
                controllerName = methodInfo.DeclaringType.Name;
            }
            else
            {
                // If the declaring type is null or compiler-generated (e.g. lambdas),
                // group the methods under the application name.
                controllerName = _environment.ApplicationName;
            }

            var apiDescription = new ApiDescription
            {
                HttpMethod = httpMethod,
                GroupName = routeEndpoint.Metadata.GetMetadata<IEndpointGroupNameMetadata>()?.EndpointGroupName,
                RelativePath = routeEndpoint.RoutePattern.RawText?.TrimStart('/'),
                ActionDescriptor = new ActionDescriptor
                {
                    DisplayName = routeEndpoint.DisplayName,
                    RouteValues =
                    {
                        ["controller"] = controllerName,
                    },
                },
            };

            foreach (var parameter in methodInfo.GetParameters())
            {
                var parameterDescription = CreateApiParameterDescription(parameter, routeEndpoint.RoutePattern);

                if (parameterDescription is null)
                {
                    continue;
                }

                apiDescription.ParameterDescriptions.Add(parameterDescription);
            }

            // Get IAcceptsMetadata.
            var acceptsMetadata = routeEndpoint.Metadata.GetMetadata<IAcceptsMetadata>();
            if (acceptsMetadata is not null)
            {
                var acceptsRequestType = acceptsMetadata.RequestType;
                var isOptional = acceptsMetadata.IsOptional;
                var parameterDescription = new ApiParameterDescription
                {
                    Name = acceptsRequestType is not null ? acceptsRequestType.Name : typeof(void).Name,
                    ModelMetadata = CreateModelMetadata(acceptsRequestType ?? typeof(void)),
                    Source = BindingSource.Body,
                    Type = acceptsRequestType ?? typeof(void),
                    IsRequired = !isOptional,
                };
                apiDescription.ParameterDescriptions.Add(parameterDescription);

                var supportedRequestFormats = apiDescription.SupportedRequestFormats;

                foreach (var contentType in acceptsMetadata.ContentTypes)
                {
                    supportedRequestFormats.Add(new ApiRequestFormat
                    {
                        MediaType = contentType
                    });
                }
            }

            AddSupportedResponseTypes(apiDescription.SupportedResponseTypes, methodInfo.ReturnType, routeEndpoint.Metadata);
            AddActionDescriptorEndpointMetadata(apiDescription.ActionDescriptor, routeEndpoint.Metadata);

            return apiDescription;
        }

        private ApiParameterDescription? CreateApiParameterDescription(ParameterInfo parameter, RoutePattern pattern)
        {
            var (source, name, allowEmpty) = GetBindingSourceAndName(parameter, pattern);

            // Services are ignored because they are not request parameters.
            // We ignore/skip body parameter because the value will be retrieved from the IAcceptsMetadata.
            if (source == BindingSource.Services || source == BindingSource.Body)
            {
                return null;
            }

            // Determine the "requiredness" based on nullability, default value or if allowEmpty is set
            var nullabilityContext = new NullabilityInfoContext();
            var nullability = nullabilityContext.Create(parameter);
            var isOptional = parameter.HasDefaultValue || nullability.ReadState != NullabilityState.NotNull || allowEmpty;

            return new ApiParameterDescription
            {
                Name = name,
                ModelMetadata = CreateModelMetadata(parameter.ParameterType),
                Source = source,
                DefaultValue = parameter.DefaultValue,
                Type = parameter.ParameterType,
                IsRequired = !isOptional
            };
        }

        // TODO: Share more of this logic with RequestDelegateFactory.CreateArgument(...) using RequestDelegateFactoryUtilities
        // which is shared source.
        private (BindingSource, string, bool) GetBindingSourceAndName(ParameterInfo parameter, RoutePattern pattern)
        {
            var attributes = parameter.GetCustomAttributes();

            if (attributes.OfType<IFromRouteMetadata>().FirstOrDefault() is { } routeAttribute)
            {
                return (BindingSource.Path, routeAttribute.Name ?? parameter.Name ?? string.Empty, false);
            }
            else if (attributes.OfType<IFromQueryMetadata>().FirstOrDefault() is { } queryAttribute)
            {
                return (BindingSource.Query, queryAttribute.Name ?? parameter.Name ?? string.Empty, false);
            }
            else if (attributes.OfType<IFromHeaderMetadata>().FirstOrDefault() is { } headerAttribute)
            {
                return (BindingSource.Header, headerAttribute.Name ?? parameter.Name ?? string.Empty, false);
            }
            else if (attributes.OfType<IFromBodyMetadata>().FirstOrDefault() is { } fromBodyAttribute)
            {
                return (BindingSource.Body, parameter.Name ?? string.Empty, fromBodyAttribute.AllowEmpty);
            }
            else if (parameter.CustomAttributes.Any(a => typeof(IFromServiceMetadata).IsAssignableFrom(a.AttributeType)) ||
                     parameter.ParameterType == typeof(HttpContext) ||
                     parameter.ParameterType == typeof(HttpRequest) ||
                     parameter.ParameterType == typeof(HttpResponse) ||
                     parameter.ParameterType == typeof(ClaimsPrincipal) ||
                     parameter.ParameterType == typeof(CancellationToken) ||
                     TryParseMethodCache.HasBindAsyncMethod(parameter) ||
                     _serviceProviderIsService?.IsService(parameter.ParameterType) == true)
            {
                return (BindingSource.Services, parameter.Name ?? string.Empty, false);
            }
            else if (parameter.ParameterType == typeof(string) || TryParseMethodCache.HasTryParseStringMethod(parameter))
            {
                // Path vs query cannot be determined by RequestDelegateFactory at startup currently because of the layering, but can be done here.
                if (parameter.Name is { } name && pattern.GetParameter(name) is not null)
                {
                    return (BindingSource.Path, name, false);
                }
                else
                {
                    return (BindingSource.Query, parameter.Name ?? string.Empty, false);
                }
            }
            else
            {
                return (BindingSource.Body, parameter.Name ?? string.Empty, false);
            }
        }

        private static void AddSupportedResponseTypes(
            IList<ApiResponseType> supportedResponseTypes,
            Type returnType,
            EndpointMetadataCollection endpointMetadata)
        {
            var responseType = returnType;

            if (AwaitableInfo.IsTypeAwaitable(responseType, out var awaitableInfo))
            {
                responseType = awaitableInfo.ResultType;
            }

            // Can't determine anything about IResults yet that's not from extra metadata. IResult<T> could help here.
            if (typeof(IResult).IsAssignableFrom(responseType))
            {
                responseType = typeof(void);
            }

            var responseMetadata = endpointMetadata.GetOrderedMetadata<IApiResponseMetadataProvider>();
            var responseTypeMetadata = endpointMetadata.GetOrderedMetadata<IProducesResponseTypeMetadata>().Where(p => p is not IApiResponseMetadataProvider);
            var errorMetadata = endpointMetadata.GetMetadata<ProducesErrorResponseTypeAttribute>();
            var defaultErrorType = errorMetadata?.Type ?? typeof(void);
            var contentTypes = new MediaTypeCollection();

            var responseMetadataProviderTypes = ApiResponseTypeProvider.ReadResponseMetadata(
                responseMetadata, responseType, defaultErrorType, contentTypes);

            var responseTypeMetadataTypes = ReadResponseTypeMetadata(responseTypeMetadata, responseType, defaultErrorType);

            var responseMetadataTypes = responseMetadataProviderTypes.Concat(responseTypeMetadataTypes);

            if (responseMetadataTypes.Any())
            {
                foreach (var apiResponseType in responseMetadataTypes)
                {
                    // void means no response type was specified by the metadata, so use whatever we inferred.
                    // ApiResponseTypeProvider should never return ApiResponseTypes with null Type, but it doesn't hurt to check.
                    if (apiResponseType.Type is null || apiResponseType.Type == typeof(void))
                    {
                        apiResponseType.Type = responseType;
                    }

                    apiResponseType.ModelMetadata = CreateModelMetadata(apiResponseType.Type);

                    if (contentTypes.Count > 0)
                    {
                        AddResponseContentTypes(apiResponseType.ApiResponseFormats, contentTypes);
                    }
                    // Only set the default response type if it hasn't already been set via a
                    // ProducesResponseTypeAttribute.
                    else if (apiResponseType.ApiResponseFormats.Count == 0 && CreateDefaultApiResponseFormat(apiResponseType.Type) is { } defaultResponseFormat)
                    {
                        apiResponseType.ApiResponseFormats.Add(defaultResponseFormat);
                    }

                    supportedResponseTypes.Add(apiResponseType);
                }
            }
            else
            {
                // Set the default response type only when none has already been set explicitly with metadata.
                var defaultApiResponseType = CreateDefaultApiResponseType(responseType);

                if (contentTypes.Count > 0)
                {
                    // If metadata provided us with response formats, use that instead of the default.
                    defaultApiResponseType.ApiResponseFormats.Clear();
                    AddResponseContentTypes(defaultApiResponseType.ApiResponseFormats, contentTypes);
                }

                supportedResponseTypes.Add(defaultApiResponseType);
            }
        }

        private static List<ApiResponseType> ReadResponseTypeMetadata(
            IEnumerable<IProducesResponseTypeMetadata> responseMetadataAttributes,
            Type? type,
            Type defaultErrorType)
        {
            var results = new Dictionary<int, ApiResponseType>();

            // Get the content type that the action explicitly set to support.
            // Walk through all 'filter' attributes in order, and allow each one to see or override
            // the results of the previous ones. This is similar to the execution path for content-negotiation.
            if (responseMetadataAttributes != null)
            {
                foreach (var metadataAttribute in responseMetadataAttributes)
                {
                    var statusCode = metadataAttribute.StatusCode;

                    var apiResponseType = new ApiResponseType
                    {
                        Type = metadataAttribute.Type,
                        StatusCode = statusCode,
                        IsDefaultResponse = metadataAttribute is IApiDefaultResponseMetadataProvider,
                    };

                    if (apiResponseType.Type == typeof(void))
                    {
                        if (type != null && (statusCode == StatusCodes.Status200OK || statusCode == StatusCodes.Status201Created))
                        {
                            // ProducesResponseTypeAttribute's constructor defaults to setting "Type" to void when no value is specified.
                            // In this event, use the action's return type for 200 or 201 status codes. This lets you decorate an action with a
                            // [ProducesResponseType(201)] instead of [ProducesResponseType(typeof(Person), 201] when typeof(Person) can be inferred
                            // from the return type.
                            apiResponseType.Type = type;
                        }
                        else if (ApiResponseTypeProvider.IsClientError(statusCode))
                        {
                            // Determine whether or not the type was provided by the user. If so, favor it over the default
                            // error type for 4xx client errors if no response type is specified..
                            var setByDefault = metadataAttribute is ProducesResponseTypeAttribute { IsResponseTypeSetByDefault: true };
                            apiResponseType.Type = setByDefault ? defaultErrorType : apiResponseType.Type;
                        }
                        else if (apiResponseType.IsDefaultResponse)
                        {
                            apiResponseType.Type = defaultErrorType;
                        }
                    }

                    var attributeContentTypes = new MediaTypeCollection();
                    metadataAttribute.SetContentTypes(attributeContentTypes);
                    ApiResponseTypeProvider.CalculateResponseFormatForType(apiResponseType, attributeContentTypes, null, null);

                    if (apiResponseType.Type != null)
                    {
                        results[apiResponseType.StatusCode] = apiResponseType;
                    }
                }
            }

            return results.Values.ToList();
        }

        private static ApiResponseType CreateDefaultApiResponseType(Type responseType)
        {
            var apiResponseType = new ApiResponseType
            {
                ModelMetadata = CreateModelMetadata(responseType),
                StatusCode = 200,
                Type = responseType,
            };

            if (CreateDefaultApiResponseFormat(responseType) is { } responseFormat)
            {
                apiResponseType.ApiResponseFormats.Add(responseFormat);
            }

            return apiResponseType;
        }

        private static ApiResponseFormat? CreateDefaultApiResponseFormat(Type responseType)
        {
            if (responseType == typeof(void))
            {
                return null;
            }
            else if (responseType == typeof(string))
            {
                // This uses HttpResponse.WriteAsync(string) method which doesn't set a content type. It could be anything,
                // but I think "text/plain" is a reasonable assumption if nothing else is specified with metadata.
                return new ApiResponseFormat { MediaType = "text/plain" };
            }
            else
            {
                // Everything else is written using HttpResponse.WriteAsJsonAsync<TValue>(T).
                return new ApiResponseFormat { MediaType = "application/json" };
            }
        }

        private static EndpointModelMetadata CreateModelMetadata(Type type) =>
            new(ModelMetadataIdentity.ForType(type));

        private static void AddResponseContentTypes(IList<ApiResponseFormat> apiResponseFormats, IReadOnlyList<string> contentTypes)
        {
            foreach (var contentType in contentTypes)
            {
                apiResponseFormats.Add(new ApiResponseFormat
                {
                    MediaType = contentType,
                });
            }
        }

        private static void AddActionDescriptorEndpointMetadata(
            ActionDescriptor actionDescriptor,
            EndpointMetadataCollection endpointMetadata)
        {
            if (endpointMetadata.Count > 0)
            {
                // ActionDescriptor.EndpointMetadata is an empty array by
                // default so need to add the metadata into a new list.
                actionDescriptor.EndpointMetadata = new List<object>(endpointMetadata);
            }
        }
    }
}
