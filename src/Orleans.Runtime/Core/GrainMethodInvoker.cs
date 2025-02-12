using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using Orleans.Serialization;
using Orleans.Serialization.Invocation;

namespace Orleans.Runtime
{
    /// <summary>
    /// Invokes a request on a grain.
    /// </summary>
    internal class GrainMethodInvoker : IIncomingGrainCallContext, IMethodArguments
    {
        private readonly IInvokable request;
        private readonly List<IIncomingGrainCallFilter> filters;
        private readonly InterfaceToImplementationMappingCache interfaceToImplementationMapping;
        private readonly DeepCopier<Response> responseCopier;
        private readonly IGrainContext grainContext;
        private int stage;

        /// <summary>
        /// Initializes a new instance of the <see cref="GrainMethodInvoker"/> class.
        /// </summary>
        /// <param name="grainContext">The grain.</param>
        /// <param name="request">The request.</param>
        /// <param name="filters">The invocation interceptors.</param>
        /// <param name="interfaceToImplementationMapping">The implementation map.</param>
        /// <param name="responseCopier">The response copier.</param>
        public GrainMethodInvoker(
            IGrainContext grainContext,
            IInvokable request,
            List<IIncomingGrainCallFilter> filters,
            InterfaceToImplementationMappingCache interfaceToImplementationMapping,
            DeepCopier<Response> responseCopier)
        {
            this.request = request;
            this.grainContext = grainContext;
            this.filters = filters;
            this.interfaceToImplementationMapping = interfaceToImplementationMapping;
            this.responseCopier = responseCopier;
        }

        /// <inheritdoc />
        public object Grain => grainContext.GrainInstance;

        /// <inheritdoc />
        public MethodInfo Method => request.Method;

        /// <inheritdoc />
        public MethodInfo InterfaceMethod => request.Method;

        /// <inheritdoc />
        public MethodInfo ImplementationMethod => GetMethodEntry().ImplementationMethod;

        /// <inheritdoc />
        public IMethodArguments Arguments => this;
        
        /// <inheritdoc />
        public object Result
        {
            get => Response switch
            {
                { Exception: null } response => response.Result,
                _ => null
            };
            set => Response = Response.FromResult(value);
        }

        /// <inheritdoc />
        public Response Response { get; set; }

        object IMethodArguments.this[int index]
        {
            get => request.GetArgument<object>(index);
            set => request.SetArgument(index, value);
        }

        T IMethodArguments.GetArgument<T>(int index) => request.GetArgument<T>(index);

        void IMethodArguments.SetArgument<T>(int index, T value) => request.SetArgument(index, value);

        int IMethodArguments.Length => request.ArgumentCount;

        /// <inheritdoc />
        public async Task Invoke()
        {
            try
            {
                // Execute each stage in the pipeline. Each successive call to this method will invoke the next stage.
                // Stages which are not implemented (eg, because the user has not specified an interceptor) are skipped.
                var numFilters = filters.Count;
                if (stage < numFilters)
                {
                    // Call each of the specified interceptors.
                    var systemWideFilter = this.filters[stage];
                    stage++;
                    await systemWideFilter.Invoke(this);
                    return;
                }

                if (stage == numFilters)
                {
                    stage++;

                    // Grain-level invoker, if present.
                    if (this.Grain is IIncomingGrainCallFilter grainClassLevelFilter)
                    {
                        await grainClassLevelFilter.Invoke(this);
                        return;
                    }
                }

                if (stage == numFilters + 1)
                {
                    // Finally call the root-level invoker.
                    stage++;
                    this.Response = await request.Invoke();

                    // Propagate exceptions to other filters.
                    if (this.Response.Exception is { } exception)
                    {
                        ExceptionDispatchInfo.Capture(exception).Throw();
                    }

                    this.Response = this.responseCopier.Copy(this.Response);

                    return;
                }
            }
            finally
            {
                stage--;
            }

            // If this method has been called more than the expected number of times, that is invalid.
            ThrowInvalidCall();
        }

        private static void ThrowInvalidCall()
        {
            throw new InvalidOperationException(
                $"{nameof(GrainMethodInvoker)}.{nameof(Invoke)}() received an invalid call.");
        }

        private InterfaceToImplementationMappingCache.Entry GetMethodEntry()
        {
            var interfaceType = this.request.InterfaceType;
            var implementationType = this.request.GetTarget<object>().GetType();

            // Get or create the implementation map for this object.
            var implementationMap = interfaceToImplementationMapping.GetOrCreate(
                implementationType,
                interfaceType);

            // Get the method info for the method being invoked.
            if (!implementationMap.TryGetValue(request.Method, out var method))
            {
                return default;
            }

            if (method.InterfaceMethod is null)
            {
                return default;
            }

            return method;
        }
    }
}
