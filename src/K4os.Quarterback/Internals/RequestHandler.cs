using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using K4os.Quarterback.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace K4os.Quarterback.Internals
{
	internal static class RequestHandler
	{
		private const BindingFlags DefaultBindingFlags =
			BindingFlags.Static | BindingFlags.NonPublic;

		public static Task<object> Request(
			IServiceProvider provider,
			Type requestType, Type responseType, object request,
			CancellationToken token)
		{
			var handlerType = GetHandlerType(requestType, responseType);
			var handler = provider.GetRequiredService(handlerType);
			var handlerInvoker = GetHandlerInvoker(requestType, responseType);
			var actualHandlerType = handler.GetType();
			var pipelineType = GetPipelineType(actualHandlerType, requestType, responseType);
			var pipeline = provider.GetServices(pipelineType).AsArray();
			return Execute(pipelineType, pipeline, handler, handlerInvoker, request, token);
		}

		public static Task<object> Request<TRequest>(
			IServiceProvider provider, Type requestType, TRequest request, CancellationToken token)
		{
			var handlerType = GetHandlerType(requestType);
			var handler = provider.GetRequiredService(handlerType);
			var actualHandlerType = handler.GetType();
			var handlerInfo = GetHandlerInfo(requestType, actualHandlerType);
			var handlerInvoker = handlerInfo.Invoker.Required(nameof(HandlerInfo.Invoker));
			var responseType = handlerInfo.ResponseType;
			var pipelineType = GetPipelineType(actualHandlerType, requestType, responseType);
			var pipeline = provider.GetServices(pipelineType).AsArray();
			return Execute(pipelineType, pipeline, handler, handlerInvoker, request, token);
		}

		private static Task<object> Execute<TRequest>(
			Type pipelineType, IReadOnlyList<object> pipeline,
			object handler, HandlerInvoker handlerInvoker, TRequest request,
			CancellationToken token)
		{
			if (request is null)
				throw new ArgumentNullException(nameof(request));

			return pipeline.Count <= 0 
				? handlerInvoker(handler, request, token) 
				: ExecutePipeline(pipelineType, pipeline, handler, handlerInvoker, request, token);
		}

		private static Task<object> ExecutePipeline<TRequest>(
			Type pipelineType, IReadOnlyList<object> pipeline, 
			object handler, HandlerInvoker handlerInvoker,
			TRequest request, CancellationToken token)
		{
			Debug.Assert(request is not null);
			
			Func<Task<object>> next = () => handlerInvoker(handler, request!, token);
			var pipelineInvoker = GetPipelineInvoker(pipelineType);

			Func<Task<object>> Combine(object wrapper, Func<Task<object>> rest) =>
				() => pipelineInvoker(wrapper, handler, request!, rest, token);

			for (var i = pipeline.Count - 1; i >= 0; i--)
				next = Combine(pipeline[i], next);

			return next();
		}

		private static readonly ConcurrentDictionary<(Type, Type), Type>
			HandlerTypes2 = new();

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static Type GetHandlerType(Type requestType, Type responseType) => 
			GetHandlerType((requestType, responseType));

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static Type GetHandlerType((Type requestType, Type responseType) types) =>
			HandlerTypes2.GetOrNull(types) ??
			HandlerTypes2.GetOrAdd(types, NewHandlerType);
		
		private static Type NewHandlerType((Type requestType, Type responseType) types) =>
			typeof(IRequestHandler<,>).MakeGenericType(types.requestType, types.responseType);

		private static readonly ConcurrentDictionary<Type, Type>
			HandlerTypes1 = new();

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static Type GetHandlerType(Type requestType) =>
			HandlerTypes1.GetOrNull(requestType) ??
			HandlerTypes1.GetOrAdd(requestType, NewHandlerType);

		private static Type NewHandlerType(Type requestType) =>
#pragma warning disable 618
			typeof(IRequestHandlerPartialMarker<>).MakeGenericType(requestType);
#pragma warning restore 618

		private delegate Task<object> HandlerInvoker(
			object handler, object request, CancellationToken token);

		private class HandlerInfo
		{
			public Type RequestType { get; }
			public int RequestDistance { get; }
			public Type ResponseType { get; }
			public HandlerInvoker? Invoker { get; set; }

			public HandlerInfo(Type requestType, int requestDistance, Type responseType)
			{
				RequestType = requestType;
				RequestDistance = requestDistance;
				ResponseType = responseType;
			}
		}

		private static readonly ConcurrentDictionary<(Type, Type), HandlerInfo> 
			HandlerInfos = new();

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static HandlerInfo GetHandlerInfo(Type requestType, Type actualHandlerType) => 
			GetHandlerInfo((requestType, actualHandlerType));

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static HandlerInfo GetHandlerInfo(
			(Type requestType, Type actualHandlerType) types) =>
			HandlerInfos.GetOrNull(types) ??
			HandlerInfos.GetOrAdd(types, NewHandlerInfo);

		private static HandlerInfo NewHandlerInfo((Type, Type) types)
		{
			var (requestType, actualHandlerType) = types;

			HandlerInfo? TryMatchRequest(Type interfaceType)
			{
				if (!interfaceType.IsGenericType)
					return null;
				if (interfaceType.GetGenericTypeDefinition() != typeof(IRequestHandler<,>))
					return null;

				var genericArgs = interfaceType.GetGenericArguments();
				var declaredRequestType = genericArgs[0];

				if (!requestType.InheritsFrom(declaredRequestType))
					return null;

				var distance = requestType.DistanceFrom(declaredRequestType);
				var responseType = genericArgs[1];

				return new HandlerInfo(declaredRequestType, distance, responseType);
			}

			var match = actualHandlerType
				.GetInterfaces()
				.Select(TryMatchRequest)
				.NoNulls()
				.MinBy(r => r.RequestDistance);

			if (match is null)
				throw new ArgumentException(
					string.Format(
						"No matching handler could be found for {0} in {1}",
						requestType.GetFriendlyName(),
						actualHandlerType.GetFriendlyName()));

			match.Invoker = GetHandlerInvoker(match.RequestType, match.ResponseType);

			return match;
		}

		private static readonly ConcurrentDictionary<(Type, Type), HandlerInvoker> 
			HandlerInvokers = new();

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static HandlerInvoker GetHandlerInvoker(Type requestType, Type responseType) => 
			GetHandlerInvoker((requestType, responseType));

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static HandlerInvoker GetHandlerInvoker(
			(Type requestType, Type responseType) types) =>
			HandlerInvokers.GetOrNull(types) ??
			HandlerInvokers.GetOrAdd(types, NewHandlerInvoker);

		private static Task<object?> UntypedHandlerInvoker<TRequest, TResponse>(
			object handler, object request, CancellationToken token) =>
			((IRequestHandler<TRequest, TResponse>) handler)
			.Handle((TRequest) request, token)
			.Box();

		private static HandlerInvoker NewHandlerInvoker(
			(Type requestType, Type responseType) types)
		{
			var (requestType, responseType) = types;
			var handlerArg = Expression.Parameter(typeof(object));
			var requestArg = Expression.Parameter(typeof(object));
			var tokenArg = Expression.Parameter(typeof(CancellationToken));
			var handleMethod = typeof(RequestHandler)
				.GetMethod(nameof(UntypedHandlerInvoker), DefaultBindingFlags)
				.Required(nameof(UntypedHandlerInvoker))
				.MakeGenericMethod(requestType, responseType);
			var body =
				Expression.Call(handleMethod, handlerArg, requestArg, tokenArg);
			var lambda = Expression.Lambda<HandlerInvoker>(
				body, handlerArg, requestArg, tokenArg);
			return lambda.Compile();
		}

		private static readonly ConcurrentDictionary<(Type, Type, Type), Type>
			PipelineTypes = new();

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static Type GetPipelineType(
			Type handlerType, Type requestType, Type responseType) =>
			GetPipelineType((handlerType, requestType, responseType));

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static Type GetPipelineType(
			(Type handlerType, Type requestType, Type responseType) types) =>
			PipelineTypes.GetOrNull(types) ??
			PipelineTypes.GetOrAdd(types, NewPipelineType);

		private static Type NewPipelineType(
			(Type handlerType, Type requestType, Type responseType) types)
		{
			var (handlerType, requestType, responseType) = types;
			return typeof(IRequestPipeline<,,>).MakeGenericType(
				handlerType, requestType, responseType);
		}

		private delegate Task<object> PipelineInvoker(
			object wrapper,
			object handler, object request, Func<Task<object>> next,
			CancellationToken token);

		private static readonly ConcurrentDictionary<Type, PipelineInvoker> 
			PipelineInvokers = new();

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static PipelineInvoker GetPipelineInvoker(Type pipelineType) =>
			PipelineInvokers.GetOrNull(pipelineType) ??
			PipelineInvokers.GetOrAdd(pipelineType, NewPipelineInvoker);

		private static Task<object?> UntypedPipelineInvoker<THandler, TRequest, TResponse>(
			object wrapper,
			object handler, object request, Func<Task<object>> next,
			CancellationToken token) where THandler: IRequestHandler<TRequest, TResponse> =>
			((IRequestPipeline<THandler, TRequest, TResponse>) wrapper)
			.Handle((THandler) handler, (TRequest) request, () => next().Unbox<TResponse>(), token)
			.Box();

		private static PipelineInvoker NewPipelineInvoker(Type pipelineType)
		{
			var args = pipelineType.GetGenericArguments();
			var (handlerType, requestType, responseType) = (args[0], args[1], args[2]);
			var wrapperArg = Expression.Parameter(typeof(object));
			var handlerArg = Expression.Parameter(typeof(object));
			var requestArg = Expression.Parameter(typeof(object));
			var nextArg = Expression.Parameter(typeof(Func<Task<object>>));
			var tokenArg = Expression.Parameter(typeof(CancellationToken));
			var method = typeof(RequestHandler)
				.GetMethod(nameof(UntypedPipelineInvoker), DefaultBindingFlags)
				.Required(nameof(UntypedPipelineInvoker))
				.MakeGenericMethod(handlerType, requestType, responseType);
			var body = Expression.Call(
				method, wrapperArg, handlerArg, requestArg, nextArg, tokenArg);
			var lambda = Expression.Lambda<PipelineInvoker>(
				body, wrapperArg, handlerArg, requestArg, nextArg, tokenArg);
			return lambda.Compile();
		}
	}
}
