﻿using System;
using System.Collections.Generic;
using System.Linq;
using AsyncGenerator.Extensions;
using AsyncGenerator.Extensions.Internal;
using AsyncGenerator.Internal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AsyncGenerator.Analyzation.Internal
{
	internal partial class ProjectAnalyzer
	{
		/// <summary>
		/// Set all method data dependencies to be also async
		/// </summary>
		/// <param name="asyncMethodData">Method data that is marked to be async</param>
		/// <param name="toProcessMethodData">All method data that needs to be processed</param>
		private void PostAnalyzeAsyncMethodData(MethodData asyncMethodData, ISet<MethodData> toProcessMethodData)
		{
			if (!toProcessMethodData.Contains(asyncMethodData))
			{
				return;
			}
			var processingMetodData = new Queue<MethodData>();
			processingMetodData.Enqueue(asyncMethodData);
			while (processingMetodData.Any())
			{
				var currentMethodData = processingMetodData.Dequeue();
				toProcessMethodData.Remove(currentMethodData);

				// Missing methods have already calculated the CancellationTokenRequired and MethodCancellationToken in the scanning step
				if (!currentMethodData.Missing && _configuration.UseCancellationTokens)
				{
					// Permit the consumer to decide require the cancellation parameter
					currentMethodData.CancellationTokenRequired =
						_configuration.CancellationTokens.RequiresCancellationToken(currentMethodData.Symbol) ??
						currentMethodData.CancellationTokenRequired;
				}
				// TODO: support params
				if (currentMethodData.Symbol.Parameters.LastOrDefault()?.IsParams == true)
				{
					currentMethodData.CancellationTokenRequired = false;
					Logger.Warn($"Cancellation token parameter will not be added for method {currentMethodData.Symbol} because the last parameter" +
								$" is declared as a parameter array which is currently not supported");
				}
				if (currentMethodData.CancellationTokenRequired)
				{
					if (!currentMethodData.Missing)
					{
						currentMethodData.MethodCancellationToken = _configuration.CancellationTokens.MethodGeneration(currentMethodData);
					}
					currentMethodData.AddCancellationTokenGuards = _configuration.CancellationTokens.Guards;
				}

				CalculatePreserveReturnType(currentMethodData);

				foreach (var depFunctionData in currentMethodData.Dependencies)
				{
					var depMethodData = depFunctionData as MethodData;
					var bodyReferences = depFunctionData.BodyMethodReferences.Where(o => o.ReferenceFunctionData == currentMethodData).ToList();
					if (depMethodData != null)
					{
						// Before setting the dependency to async we need to check if there is at least one invocation that will be converted to async
						// Here we also need to consider that a method can be a dependency because is a related method
						if (depMethodData.RelatedMethods.All(o => o != currentMethodData) && bodyReferences.All(o => o.GetConversion() == ReferenceConversion.Ignore))
						{
							continue;
						}

						if (!toProcessMethodData.Contains(depMethodData))
						{
							continue;
						}
						processingMetodData.Enqueue(depMethodData);
					}
					if (depFunctionData.Conversion == MethodConversion.Ignore)
					{
						Logger.Warn($"Ignored method {depFunctionData.Symbol} has a method invocation that can be async");
						continue;
					}
					depFunctionData.ToAsync();

					if (!currentMethodData.CancellationTokenRequired)
					{
						continue;
					}
					// Propagate the CancellationTokenRequired for the dependency method data
					if (depMethodData != null)
					{
						depMethodData.CancellationTokenRequired |= currentMethodData.CancellationTokenRequired;
					}
				}
			}
		}

		/// <summary>
		/// Skip wrapping a method into a try/catch only when we have one statement (except preconditions) that is an invocation
		/// which returns a Task. This statement must have only one invocation.
		/// </summary>
		private void CalculateWrapInTryCatch(FunctionData functionData)
		{
			var functionDataBody = functionData.GetBodyNode() as BlockSyntax;
			if (functionDataBody == null || !functionDataBody.Statements.Any() || functionData.SplitTail)
			{
				return;
			}
			if (functionDataBody.Statements.Count != functionData.Preconditions.Count + 1)
			{
				functionData.WrapInTryCatch = true;
				return;
			}
			// Do not look into child functions
			var statements = functionDataBody.Statements
				.First(o => !functionData.Preconditions.Contains(o))
				.DescendantNodesAndSelf(o => !o.IsFunction())
				.OfType<StatementSyntax>()
				.ToList();
			if (statements.Count != 1)
			{
				functionData.WrapInTryCatch = true;
				return;
			}
			var lastStatement = statements[0];
			var exprs = lastStatement?
				.DescendantNodes(o => !(o.IsFunction() || o.IsKind(SyntaxKind.DefaultExpression)))
				.OfType<ExpressionSyntax>()
				.ToList();
			if (exprs?.Count == 1)
			{
				var expr = exprs[0];
				if (expr is LiteralExpressionSyntax || expr is DefaultExpressionSyntax)
					return;
			}
			var invocationExps = exprs?.OfType<InvocationExpressionSyntax>().ToList();
			if (invocationExps?.Count != 1)
			{
				functionData.WrapInTryCatch = true;
				return;
			}
			var invocationExpr = invocationExps[0];
			var refData = functionData.BodyMethodReferences.FirstOrDefault(o => o.ReferenceNode == invocationExpr);
			if (refData == null)
			{
				functionData.WrapInTryCatch = true;
				return;
			}
			if (refData.GetConversion() == ReferenceConversion.Ignore || refData.ReferenceAsyncSymbols.Any(o => o.ReturnsVoid || !o.ReturnType.IsTaskType()))
			{
				functionData.WrapInTryCatch = true;
			}
		}

		private void CalculatePreserveReturnType(MethodData methodData)
		{
			// Shall not wrap the return type into Task when all async invocations do not return a task. Here we mark only methods that do not contain 
			// any references to internal methods
			if (!methodData.RelatedMethods.Any() &&
				methodData.BodyMethodReferences
					.Where(o => o.ArgumentOfFunctionInvocation == null)
					.All(o =>
						o.GetConversion() == ReferenceConversion.ToAsync &&
						o.ReferenceFunctionData == null &&
						!o.AsyncCounterpartSymbol.ReturnType.IsTaskType()))
			{
				methodData.PreserveReturnType = _configuration.PreserveReturnType(methodData.Symbol);
			}
		}

		private void ValidateMethodCancellationToken(MethodData methodData)
		{
			var methodGeneration = methodData.MethodCancellationToken.GetValueOrDefault();

			if (!methodGeneration.HasFlag(MethodCancellationToken.DefaultParameter) &&
			    !methodGeneration.HasFlag(MethodCancellationToken.Parameter))
			{
				methodGeneration |= MethodCancellationToken.DefaultParameter;
				Logger.Warn($"Invalid MethodGeneration option '{methodGeneration}' for method '{methodData.Symbol}'. " +
				            "The method must have atleast Parameter or DefaultParameter option set. " +
				            "DefaultParameter option will be added.");
			}
			else if (methodGeneration.HasFlag(MethodCancellationToken.DefaultParameter) &&
					 methodGeneration.HasFlag(MethodCancellationToken.Parameter))
			{
				methodGeneration &= ~MethodCancellationToken.Parameter;
				Logger.Warn($"Invalid MethodGeneration option '{methodGeneration}' for method '{methodData.Symbol}'. " +
				            "The method cannot have Parameter and DefaultParameter options set at once. " +
				            "Parameter option will be removed.");
			}
			if (methodGeneration.HasFlag(MethodCancellationToken.NoParameterForward) &&
			    methodGeneration.HasFlag(MethodCancellationToken.SealedNoParameterForward))
			{
				methodGeneration &= ~MethodCancellationToken.NoParameterForward;
				Logger.Warn($"Invalid MethodGeneration option '{methodGeneration}' for method '{methodData.Symbol}'. " +
				            "The method cannot have NoParameterForward and SealedNoParameterForward options set at once. " +
				            "NoParameterForward option will be removed.");
			}
			if (methodGeneration.HasFlag(MethodCancellationToken.DefaultParameter) &&
			    (
					methodGeneration.HasFlag(MethodCancellationToken.NoParameterForward) ||
					methodGeneration.HasFlag(MethodCancellationToken.SealedNoParameterForward)
				)
			)
			{
				methodGeneration = MethodCancellationToken.DefaultParameter;
				Logger.Warn($"Invalid MethodGeneration option '{methodGeneration}' for method '{methodData.Symbol}'. " +
				            "The DefaultParameter option cannot be combined with NoParameterForward or SealedNoParameterForward option. " +
				            "NoParameterForward and SealedNoParameterForward options will be removed.");
			}

			// Explicit implementor can have only Parameter combined with NoParameterForward or SealedNoParameterForward
			if (methodData.Symbol.ExplicitInterfaceImplementations.Any() && !methodGeneration.HasFlag(MethodCancellationToken.Parameter))
			{
				methodData.MethodCancellationToken = MethodCancellationToken.Parameter;
				Logger.Warn($"Invalid MethodGeneration option '{methodGeneration}' for method '{methodData.Symbol}'. " +
				            "Explicit implementor can have only Parameter option combined with NoParameterForward or SealedNoParameterForward option. " +
				            $"The MethodGeneration will be set to '{methodData.MethodCancellationToken}'");
				return;
			}

			// Interface method can have only Parameter or DefaultParameter
			if (methodData.InterfaceMethod && methodGeneration.HasFlag(MethodCancellationToken.Parameter) &&
				(
					methodGeneration.HasFlag(MethodCancellationToken.NoParameterForward) ||
					methodGeneration.HasFlag(MethodCancellationToken.SealedNoParameterForward)
				)
			)
			{
				methodData.MethodCancellationToken = MethodCancellationToken.Parameter;
				Logger.Warn($"Invalid MethodGeneration option '{methodGeneration}' for method '{methodData.Symbol}'. " +
							"Interface method can have NoParameterForward or SealedNoParameterForward option. " +
				            $"The MethodGeneration will be set to '{methodData.MethodCancellationToken}'");
				return;
			}
			methodData.MethodCancellationToken = methodGeneration;
		}

		private void CalculateFinalFunctionConversion(FunctionData functionData, ICollection<MethodData> asyncMethodDatas)
		{

			// Before checking the conversion of method references we have to calculate the conversion of invocations that have one or more methods passed as an argument as the current calculated conversion may be wrong
			// (eg. one of the arguments may be ignored in the post-analyze step)
			foreach (var bodyRefData in functionData.BodyMethodReferences.Values.Where(o => o.FunctionArguments != null))
			{
				var asyncCounterpart = bodyRefData.AsyncCounterpartSymbol;
				if (asyncCounterpart == null)
				{
					// TODO: define
					throw new InvalidOperationException($"AsyncCounterpartSymbol is null {bodyRefData.ReferenceNode}");
				}

				var nonAsyncArgs = bodyRefData.FunctionArguments.Where(o =>
						(o.FunctionData != null && o.FunctionData.Conversion == MethodConversion.Ignore) ||
						(o.FunctionReference != null && o.FunctionReference.GetConversion() == ReferenceConversion.Ignore))
					.Select(o => o.Index)
					.ToList();
				if (nonAsyncArgs.Any())
				{
					// Check if any of the arguments needs to be async in order to have the invocation async
					var paramOffset = asyncCounterpart.IsExtensionMethod ? 1 : 0;
					if (asyncCounterpart.Parameters
						.Where((symbol, i) => nonAsyncArgs.Contains(i + paramOffset))
						.Select(o => o.Type)
						.OfType<INamedTypeSymbol>()
						.Any(o => o.DelegateInvokeMethod != null))
					{
						bodyRefData.Ignore($"Arguments at indexes '{string.Join(", ", nonAsyncArgs)}' cannot be converted to async");
						continue;
					}
				}
				
				// Check if we need to wrap the argument into an anonymous function. We can skip anonymous functions passed as argument as they will never have an additinal parameter
				// TODO: maybe this should be moved elsewhere
				foreach (var funArgument in bodyRefData.FunctionArguments.Where(o => o.FunctionReference != null))
				{
					var argRefFunction = funArgument.FunctionReference;
					var delegateFun = (IMethodSymbol)asyncCounterpart.Parameters[funArgument.Index].Type.GetMembers("Invoke").First();
					argRefFunction.AsyncDelegateArgument = delegateFun;

					if (argRefFunction.AsyncCounterpartSymbol != null)
					{
						// TODO: check internal functions, parameters
						if (argRefFunction.ReferenceFunctionData == null && !argRefFunction.AsyncCounterpartSymbol.ReturnType.Equals(delegateFun.ReturnType))
						{
							bodyRefData.Ignore("One of the arguments does not match the with the async delegate parameter");
							break;
						}

						// If the argument is an internal method and it will be generated with an additinal parameter we need to wrap it inside a function
						if (argRefFunction.ReferenceFunctionData != null)
						{
							// TODO: check return type
							argRefFunction.WrapInsideFunction = argRefFunction.ReferenceFunctionData is MethodData argRefMethodData &&
																(argRefMethodData.CancellationTokenRequired || argRefMethodData.PreserveReturnType);
						}
						// For now we check only if the parameters matches in case the async counterpart has a cancellation token parameter
						else if (delegateFun.Parameters.Length < argRefFunction.AsyncCounterpartSymbol.Parameters.Length)
						{
							argRefFunction.WrapInsideFunction = true;
						}
					}
				}
			}

			if (functionData.Conversion != MethodConversion.Ignore && functionData.BodyMethodReferences.All(o => o.GetConversion() == ReferenceConversion.Ignore))
			{
				// A method may be already calculated to be async, but we will ignore it if the method does not have any dependency and was not explicitly set to be async
				var methodData = functionData as MethodData;
				if (methodData == null || (!methodData.Missing && !methodData.Dependencies.Any() && !asyncMethodDatas.Contains(methodData)))
				{
					functionData.Ignore("Does not have any async invocations");
				}
				return;
			}

			if (functionData.Conversion.HasAnyFlag(MethodConversion.ToAsync, MethodConversion.Ignore))
			{
				return;
			}

			if (functionData.BodyMethodReferences.Any(o => o.GetConversion() == ReferenceConversion.ToAsync))
			{
				functionData.ToAsync();
			}
			else
			{
				functionData.Ignore("Has no async invocations");
			}
		}

		/// <summary>
		/// Calculates the final conversion for all currently not ignored method/type/namespace data
		/// </summary>
		/// <param name="documentData">All project documents</param>
		private void PostAnalyze(IEnumerable<DocumentData> documentData)
		{
			var allNamespaceData = documentData
				.SelectMany(o => o.GetAllNamespaceDatas(m => m.Conversion != NamespaceConversion.Ignore))
				.ToList();

			// We need to take care of explictly ignored methods as we have to implicitly ignore also the related methods.
			// Here the conversion for the type is not yet calculated so we have to process all types
			foreach (var methodData in allNamespaceData
				.SelectMany(o => o.Types.Values)
				.SelectMany(o => o.GetSelfAndDescendantsTypeData())
				.SelectMany(o => o.Methods.Values.Where(m => m.ExplicitlyIgnored)))
			{
				// If an abstract method is ignored we have to ignore also the overrides otherwise we may break the functionality and the code from compiling (eg. base.Call())
				if (methodData.Symbol.IsAbstract || methodData.Symbol.IsVirtual)
				{
					foreach (var relatedMethodData in methodData.RelatedMethods.Where(i => i.RelatedMethods.All(r => r == methodData)))
					{
						if (relatedMethodData.TypeData.Conversion == TypeConversion.NewType ||
						    relatedMethodData.TypeData.Conversion == TypeConversion.Copy)
						{
							relatedMethodData.Copy();
						}
						else
						{
							relatedMethodData.Ignore($"Implicitly ignored because of the explictly ignored method {methodData.Symbol}");
							WarnLogIgnoredReason(relatedMethodData);

						}
					}
				}
				// TODO: if an override implements an interface that is not ignored, we would need to remove the override method and not ignore it
				if (!methodData.InterfaceMethod)
				{
					continue;
				}
				// If an interface method is explictly ignored then we need to implicitly ignore the related methods, 
				// but only if a related method implements just the ignored one.
				// TODO: remove override keyword if exist for the non removed related methods
				foreach (var relatedMethodData in methodData.RelatedMethods.Where(i => !i.RelatedMethods.Any(r => r != methodData && r.InterfaceMethod)))
				{
					relatedMethodData.Ignore($"Implicitly ignored because of the explictly ignored method {methodData.Symbol}");
					WarnLogIgnoredReason(relatedMethodData);
				}
			}

			// If a type data is ignored then also its method data are ignored
			var allTypeData = allNamespaceData
				.SelectMany(o => o.Types.Values)
				.SelectMany(o => o.GetSelfAndDescendantsTypeData(t => t.Conversion != TypeConversion.Ignore))
				//.Where(o => o.Conversion != TypeConversion.Ignore)
				.ToList();
			var toProcessMethodData = new HashSet<MethodData>(allTypeData
				.SelectMany(o => o.Methods.Values.Where(m => m.Conversion.HasAnyFlag(MethodConversion.ToAsync, MethodConversion.Smart, MethodConversion.Unknown))));
			//TODO: optimize steps for better performance

			// 0. Step - If cancellation tokens are enabled we should start from methods that requires a cancellation token in order to correctly propagate CancellationTokenRequired
			// to dependency methods
			if (_configuration.UseCancellationTokens || _configuration.ScanForMissingAsyncMembers != null)
			{
				var tokenMethodDatas = toProcessMethodData.Where(o => o.CancellationTokenRequired).ToList();
				foreach (var tokenMethodData in tokenMethodDatas)
				{
					if (toProcessMethodData.Count == 0)
					{
						break;
					}
					tokenMethodData.ToAsync();
					PostAnalyzeAsyncMethodData(tokenMethodData, toProcessMethodData);
				}
			}
			
			// 1. Step - Go through all async methods and set their dependencies to be also async
			// TODO: should we start from the bottom/leaf method that is async? how do we know if the method is a leaf (consider circular calls)?
			var asyncMethodDatas = toProcessMethodData.Where(o => o.Conversion.HasFlag(MethodConversion.ToAsync)).ToList();
			foreach (var asyncMethodData in asyncMethodDatas)
			{
				if (toProcessMethodData.Count == 0)
				{
					break;
				}
				PostAnalyzeAsyncMethodData(asyncMethodData, toProcessMethodData);
			}

			// 2. Step - Go through remaining methods and set them to be async if there is at least one method invocation that will get converted
			// TODO: should we start from the bottom/leaf method that is async? how do we know if the method is a leaf (consider circular calls)?
			var remainingMethodData = toProcessMethodData.ToList();
			foreach (var methodData in remainingMethodData)
			{
				if (methodData.BodyMethodReferences.Where(o => o.ArgumentOfFunctionInvocation == null).All(o => o.GetConversion() != ReferenceConversion.ToAsync))
				{
					continue;
				}
				if (methodData.Conversion == MethodConversion.Ignore)
				{
					Logger.Warn($"Ignored method {methodData.Symbol} has a method invocation that can be async");
					continue;
				}
				methodData.ToAsync();
				// Set all dependencies to be async for the newly discovered async method
				PostAnalyzeAsyncMethodData(methodData, toProcessMethodData);
				if (toProcessMethodData.Count == 0)
				{
					break;
				}
			}

			// 3. Step - Mark all remaining method to be ignored
			foreach (var methodData in toProcessMethodData)
			{
				if (methodData.TypeData.GetSelfAndAncestorsTypeData().Any(o => o.Conversion == TypeConversion.NewType))
				{
					// For smart methods we will have filled dependencies so we can ignore it if is not used
					if (methodData.Conversion.HasFlag(MethodConversion.Smart) && !methodData.Dependencies.Any())
					{
						methodData.Ignore("Method is never used.");
					}
					else
					{
						methodData.Conversion &= ~MethodConversion.Unknown;
						methodData.Conversion &= ~MethodConversion.Smart;
						methodData.Copy();
					}
				}
				else
				{
					methodData.Ignore("Method is never used.");
				}
				LogIgnoredReason(methodData);
			}


			// We need to calculate the final conversion for the local/anonymous functions
			foreach (var methodData in allTypeData.SelectMany(o => o.Methods.Values.Where(m => m.Conversion.HasFlag(MethodConversion.ToAsync))))
			{
				// We have to calculate the conversion from bottom to top as a body reference may depend on a child function (passed by argument)
				foreach (var childFunction in methodData.GetSelfAndDescendantsFunctions().Where(o => o.GetBodyNode() != null).OrderByDescending(o => o.GetBodyNode().SpanStart))
				{
					CalculateFinalFunctionConversion(childFunction, asyncMethodDatas);
				}

				// Update PassCancellationToken for all body function references that requires a cancellation token
				if (_configuration.UseCancellationTokens || _configuration.ScanForMissingAsyncMembers != null)
				{
					ValidateMethodCancellationToken(methodData);

					foreach (var functionRefData in methodData.GetSelfAndDescendantsFunctions()
						.SelectMany(o => o.BodyMethodReferences.Where(r => r.GetConversion() == ReferenceConversion.ToAsync)))
					{
						if (functionRefData.ReferenceFunctionData != null)
						{
							var refMethodData = functionRefData.ReferenceFunctionData.GetMethodData();
							functionRefData.PassCancellationToken = refMethodData.CancellationTokenRequired;
						}
						if (!methodData.CancellationTokenRequired && functionRefData.CanSkipCancellationTokenArgument() == true)
						{
							functionRefData.PassCancellationToken = false; // Do not pass CancellationToken.None if the parameter is optional
						}
					}
				}
			}

			// 4. Step - Calculate the final type conversion
			foreach (var typeData in allTypeData)
			{
				if (typeData.Conversion == TypeConversion.Ignore)
				{
					continue;
				}
				// A type can be ignored only if it has no async methods that will get converted
				if (typeData.GetSelfAndDescendantsTypeData().All(t => t.Methods.Values.All(o => o.Conversion == MethodConversion.Ignore || o.Conversion == MethodConversion.Copy)))
				{
					if (typeData.ParentTypeData?.Conversion == TypeConversion.NewType)
					{
						typeData.Copy();
					}
					else
					{
						typeData.Ignore("Has no async methods");
					}
				}
				else if(typeData.Conversion == TypeConversion.Unknown)
				{
					typeData.Conversion = TypeConversion.Partial;
				}
			}

			// 5. Step - Calculate the final namespace conversion
			foreach (var namespaceData in allNamespaceData)
			{
				if (namespaceData.Conversion != NamespaceConversion.Unknown)
				{
					continue;
				}
				// A type can be ignored only if it has no async methods that will get converted
				if (namespaceData.GetSelfAndDescendantsNamespaceData().All(t => t.Types.Values.All(o => o.Conversion == TypeConversion.Ignore)))
				{
					namespaceData.Conversion = NamespaceConversion.Ignore;
				}
				else
				{
					namespaceData.Conversion = NamespaceConversion.Generate;
				}
			}

			// 6. Step - For all async methods check for preconditions. Search only statements that its end location is lower that the first async method reference
			foreach (var functionData in allTypeData.Where(o => o.Conversion != TypeConversion.Ignore)
				.SelectMany(o => o.Methods.Values.Where(m => m.Conversion.HasFlag(MethodConversion.ToAsync)))
				.SelectMany(o => o.GetSelfAndDescendantsFunctions()))
			{
				CalculateFinalFlags(functionData);
			}
		}


		private void CalculateFinalFlags(FunctionData functionData)
		{
			if (functionData.GetBodyNode() == null)
			{
				return;
			}
			var methodData = functionData as MethodData;
			var asyncMethodReferences = functionData.BodyMethodReferences
				.Where(o => o.GetConversion() == ReferenceConversion.ToAsync)
				.ToList();
			var nonArgumentReferences = functionData.BodyMethodReferences
				.Where(o => o.ArgumentOfFunctionInvocation == null)
				.ToList();
			// Calculate the final reference AwaitInvocation, we can skip await if all async invocations are returned and the return type matches
			// or we have only one async invocation that is the last to be invoked
			// Invocations in synchronized methods must be awaited to mimic the same behavior as their sync counterparts
			if (methodData == null || !methodData.MustRunSynchronized)
			{
				var canSkipAwaits = true;
				// Skip functions that are passed as arguments 
				foreach (var methodReference in nonArgumentReferences)
				{
					if (methodReference.GetConversion() == ReferenceConversion.Ignore)
					{
						methodReference.AwaitInvocation = false;
						continue;
					}
					if (methodReference.AwaitInvocation == false)
					{
						continue;
					}

					if (!methodReference.UseAsReturnValue && !methodReference.LastInvocation)
					{
						canSkipAwaits = false;
						break;
					}
					var referenceFunctionData = methodReference.FunctionData;

					if (methodReference.LastInvocation && referenceFunctionData.Symbol.ReturnsVoid && (
						    (methodReference.ReferenceAsyncSymbols.Any() && methodReference.ReferenceAsyncSymbols.All(o => o.ReturnType.IsTaskType())) ||
						    methodReference.ReferenceFunctionData?.Conversion.HasFlag(MethodConversion.ToAsync) == true
					    ))
					{
						continue;
					}

					var isReturnTypeTask = methodReference.ReferenceSymbol.ReturnType.IsTaskType();
					// We need to check the return value of the async counterpart
					// eg. Task<IList<string>> to Task<IEnumerable<string>>, Task<long> -> Task<int> are not valid
					// eg. Task<int> to Task is valid
					if (!isReturnTypeTask &&
					    (
						    (
							    methodReference.ReferenceAsyncSymbols.Any() &&
							    !methodReference.ReferenceAsyncSymbols.All(o =>
							    {
								    var returnType = o.ReturnType as INamedTypeSymbol;
								    if (returnType == null || !returnType.IsGenericType)
								    {
									    return o.ReturnType.IsAwaitRequired(referenceFunctionData.Symbol.ReturnType);
								    }
								    return returnType.TypeArguments.First().IsAwaitRequired(referenceFunctionData.Symbol.ReturnType);
							    })
						    ) ||
						    (
							    methodReference.ReferenceFunctionData != null &&
							    !methodReference.ReferenceFunctionData.Symbol.ReturnType.IsAwaitRequired(referenceFunctionData.Symbol.ReturnType)
						    )
					    )
					)
					{
						canSkipAwaits = false;
						break;
					}
				}
				if (canSkipAwaits)
				{
					foreach (var methodReference in asyncMethodReferences.Where(o => o.ArgumentOfFunctionInvocation == null))
					{
						// If the async counterpart of a method reference do not return a task we cannot set UseAsReturnValue to true
						if (methodReference.AwaitInvocation != false)
						{
							methodReference.UseAsReturnValue = true;
						}
						methodReference.AwaitInvocation = false;
					}
				}
			}

			var functionBody = functionData.GetBodyNode();
			// If the method has a block body
			if (functionBody is BlockSyntax functionBlockBody)
			{
				// Some async methods may not have any async invocations because were forced to be async (overloads)
				var methodRefSpan = asyncMethodReferences
					.Select(o => o.ReferenceLocation.Location)
					.OrderBy(o => o.SourceSpan.Start)
					.FirstOrDefault();
				var semanticModel = functionData.TypeData.NamespaceData.DocumentData.SemanticModel;
				// Search for preconditions until a statement has not been qualified as a precondition or we encounter an async invocation
				// The faulted property is set to true when the first statement is a throw statement
				foreach (var statement in functionBlockBody.Statements)
				{
					if (methodRefSpan != null && statement.Span.End > methodRefSpan.SourceSpan.Start)
					{
						break;
					}
					if (!_configuration.PreconditionCheckers.Any(o => o.IsPrecondition(statement, semanticModel)))
					{
						functionData.Faulted = statement.IsKind(SyntaxKind.ThrowStatement);
						break;
					}
					functionData.Preconditions.Add(statement);
				}

				// A method shall be tail splitted when has at least one precondition and there is at least one awaitable invocation
				if (functionData.Preconditions.Any() && functionData.BodyMethodReferences.Any(o => o.AwaitInvocation == true))
				{
					functionData.SplitTail = true;
				}
			}
			else if(functionBody is ArrowExpressionClauseSyntax functionArrowBody)
			{
				functionData.Faulted = functionArrowBody.IsKind(SyntaxKind.ThrowExpression);
			}

			// The async keyword shall be omitted when the method does not have any awaitable invocation or we have to tail split
			if (functionData.SplitTail || !nonArgumentReferences.Any(o => o.GetConversion() == ReferenceConversion.ToAsync && o.AwaitInvocation == true))
			{
				functionData.OmitAsync = true;
			}

			// TODO: what about anonymous functions
			if (methodData != null && functionData.OmitAsync)
			{
				CalculatePreserveReturnType(methodData);
			}

			// When the async keyword is omitted and the method is not faulted we need to calculate if the method body shall be wrapped in a try/catch block
			// Also we do need to wrap into a try/catch when the return type remains the same
			if (!functionData.Faulted && !functionData.PreserveReturnType && functionData.OmitAsync)
			{
				// For sync forwarding we will always wrap into try catch
				if (methodData != null && methodData.BodyMethodReferences.All(o => o.GetConversion() == ReferenceConversion.Ignore) && _configuration.CallForwarding(functionData.Symbol))
				{
					methodData.WrapInTryCatch = true;
					methodData.ForwardCall = true;
				}
				else
				{
					CalculateWrapInTryCatch(functionData);
				}

			}
		}
	}
}
