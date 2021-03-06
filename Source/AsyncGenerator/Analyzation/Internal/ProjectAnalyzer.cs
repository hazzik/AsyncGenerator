﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.InteropServices.ComTypes;
using System.Threading.Tasks;
using AsyncGenerator.Configuration;
using AsyncGenerator.Configuration.Internal;
using AsyncGenerator.Internal;
using log4net;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Document = Microsoft.CodeAnalysis.Document;
using IMethodSymbol = Microsoft.CodeAnalysis.IMethodSymbol;
using Project = Microsoft.CodeAnalysis.Project;
using Solution = Microsoft.CodeAnalysis.Solution;

namespace AsyncGenerator.Analyzation.Internal
{
	internal partial class ProjectAnalyzer
	{
		private static readonly ILog Logger = LogManager.GetLogger(typeof(ProjectAnalyzer));

		private IImmutableSet<Document> _analyzeDocuments;
		private IImmutableSet<Project> _analyzeProjects;
		private ProjectAnalyzeConfiguration _configuration;
		private Solution _solution;
		private readonly ConcurrentDictionary<ITypeSymbol, ConcurrentDictionary<IMethodSymbol, ConcurrentDictionary<AsyncCounterpartsSearchOptions, HashSet<IMethodSymbol>>>> _methodByTypeAsyncConterparts = 
			new ConcurrentDictionary<ITypeSymbol, ConcurrentDictionary<IMethodSymbol, ConcurrentDictionary<AsyncCounterpartsSearchOptions, HashSet<IMethodSymbol>>>>();
		private readonly ConcurrentDictionary<IMethodSymbol, ConcurrentDictionary<AsyncCounterpartsSearchOptions, HashSet<IMethodSymbol>>> _methodAsyncConterparts =
			new ConcurrentDictionary<IMethodSymbol, ConcurrentDictionary<AsyncCounterpartsSearchOptions, HashSet<IMethodSymbol>>>();

		public ProjectAnalyzer(ProjectData projectData)
		{
			ProjectData = projectData;
		}

		public ProjectData ProjectData { get; }

		public async Task<IProjectAnalyzationResult> Analyze()
		{
			Setup();

			// 1. Step - Parse all documents inside the project and create a DocumentData for each
			Logger.Info("Parsing documents started");
			DocumentData[] documentData;
			if (_configuration.RunInParallel)
			{
				documentData = await Task.WhenAll(_analyzeDocuments.Select(o => ProjectData.CreateDocumentData(o)))
					.ConfigureAwait(false);
			}
			else
			{
				documentData = new DocumentData[_analyzeDocuments.Count];
				var i = 0;
				foreach (var analyzeDocument in _analyzeDocuments)
				{
					documentData[i] = await ProjectData.CreateDocumentData(analyzeDocument).ConfigureAwait(false);
					i++;
				}
			}
			Logger.Info("Parsing documents completed");

			// 2. Step - Each method in a document will be pre-analyzed and saved in a structural tree
			Logger.Info("Pre-analyzing documents started");
			if (_configuration.RunInParallel)
			{
				Parallel.ForEach(documentData, PreAnalyzeDocumentData);
			}
			else
			{
				foreach (var item in documentData)
				{
					PreAnalyzeDocumentData(item);
				}
			}
			Logger.Info("Pre-analyzing documents completed");

			// 3. Step - Find all references for each method and optionally scan its body for async counterparts
			Logger.Info("Scanning references started");
			if (_configuration.RunInParallel)
			{
				await Task.WhenAll(documentData.Select(ScanDocumentData)).ConfigureAwait(false);
			}
			else
			{
				foreach (var item in documentData)
				{
					await ScanDocumentData(item).ConfigureAwait(false);
				}
			}
			Logger.Info("Scanning references completed");

			// 4. Step - Analyze all references found in the previous step
			Logger.Info("Analyzing documents started");
			if (_configuration.RunInParallel)
			{
				Parallel.ForEach(documentData, AnalyzeDocumentData);
			}
			else
			{
				foreach (var item in documentData)
				{
					AnalyzeDocumentData(item);
				}
			}
			Logger.Info("Analyzing documents completed");

			// 5. Step - Calculate the final conversion for all method data
			Logger.Info("Post-analyzing documents started");
			PostAnalyze(documentData);
			Logger.Info("Post-analyzing documents completed");

			return ProjectData;
		}

		private bool CanProcessDocument(Document doc)
		{
			if (doc.Project != ProjectData.Project)
			{
				return false;
			}
			return _analyzeDocuments.Contains(doc);
		}

		private IEnumerable<IMethodSymbol> GetAsyncCounterparts(IMethodSymbol methodSymbol, ITypeSymbol invokedFromType, AsyncCounterpartsSearchOptions options, bool onlyNew = false)
		{
			if (invokedFromType == null)
			{
				return GetAsyncCounterparts(methodSymbol, options, onlyNew);
			}
			var typeDict = _methodByTypeAsyncConterparts.GetOrAdd(invokedFromType.OriginalDefinition, new ConcurrentDictionary<IMethodSymbol, ConcurrentDictionary<AsyncCounterpartsSearchOptions, HashSet<IMethodSymbol>>>());
			return GetAsyncCounterparts(typeDict, methodSymbol, invokedFromType.OriginalDefinition, options, onlyNew);
		}

		private IEnumerable<IMethodSymbol> GetAsyncCounterparts(IMethodSymbol methodSymbol, AsyncCounterpartsSearchOptions options, bool onlyNew = false)
		{
			return GetAsyncCounterparts(_methodAsyncConterparts, methodSymbol, null, options, onlyNew);
		}

		private IEnumerable<IMethodSymbol> GetAsyncCounterparts(ConcurrentDictionary<IMethodSymbol, ConcurrentDictionary<AsyncCounterpartsSearchOptions, HashSet<IMethodSymbol>>> asyncCounterparts, 
			IMethodSymbol methodSymbol, ITypeSymbol invokedFromType, AsyncCounterpartsSearchOptions options, bool onlyNew = false)
		{
			var dict = asyncCounterparts.GetOrAdd(methodSymbol, new ConcurrentDictionary<AsyncCounterpartsSearchOptions, HashSet<IMethodSymbol>>());
			HashSet<IMethodSymbol> asyncMethodSymbols;
			if (dict.TryGetValue(options, out asyncMethodSymbols))
			{
				return onlyNew ? Enumerable.Empty<IMethodSymbol>() : asyncMethodSymbols;
			}
			asyncMethodSymbols = new HashSet<IMethodSymbol>(_configuration.FindAsyncCounterpartsFinders
				.SelectMany(o => o.FindAsyncCounterparts(methodSymbol, invokedFromType, options)));
			return dict.AddOrUpdate(
				options,
				asyncMethodSymbols,
				(k, v) =>
				{
					Logger.Debug($"Performance hit: Multiple GetAsyncCounterparts method calls for method symbol {methodSymbol}");
					return asyncMethodSymbols;
				});
		}

		/// <summary>
		/// Find all invoked methods that have an async counterpart and have not been discovered yet.
		/// </summary>
		/// <param name="methodData">The method data to be searched</param>
		/// <returns>Collection of invoked methods that have an async counterpart</returns>
		private IEnumerable<IMethodSymbol> FindNewlyInvokedMethodsWithAsyncCounterpart(MethodData methodData)
		{
			var result = new HashSet<IMethodSymbol>();
			var methodDataBody = methodData.GetBodyNode();
			if (methodDataBody == null)
			{
				return result;
			}
			var documentData = methodData.TypeData.NamespaceData.DocumentData;
			var semanticModel = documentData.SemanticModel;
			var searchOptions = AsyncCounterpartsSearchOptions.Default;
			if (_configuration.UseCancellationTokens || _configuration.ScanForMissingAsyncMembers != null)
			{
				searchOptions |= AsyncCounterpartsSearchOptions.HasCancellationToken;
			}

			foreach (var invocation in methodDataBody.DescendantNodes().OfType<InvocationExpressionSyntax>())
			{
				var methodSymbol = semanticModel.GetSymbolInfo(invocation.Expression).Symbol as IMethodSymbol;
				if (methodSymbol == null)
				{
					continue;
				}
				// TODO: lets consumer choose if he wants to have the method async

				methodSymbol = methodSymbol.OriginalDefinition;
				if (result.Contains(methodSymbol))
				{
					continue;
				}

				ITypeSymbol typeSymbol = null;
				if (invocation.Expression is SimpleNameSyntax)
				{
					typeSymbol = methodData.Symbol.ContainingType;
				}
				else if (invocation.Expression is MemberAccessExpressionSyntax memberAccessExpression)
				{
					typeSymbol = semanticModel.GetTypeInfo(memberAccessExpression.Expression).Type;
				}

				// Add method only if new
				if (GetAsyncCounterparts(methodSymbol, typeSymbol, searchOptions, true).Any())
				{
					result.Add(methodSymbol);
				}
				if (!GetAsyncCounterparts(methodSymbol, typeSymbol, searchOptions).Any())
				{
					continue;
				}
				// Check if there is any method passed as argument that have also an async counterpart
				foreach (var argument in invocation.ArgumentList.Arguments
					.Where(o => o.Expression.IsKind(SyntaxKind.SimpleMemberAccessExpression) || o.Expression.IsKind(SyntaxKind.IdentifierName)))
				{
					var argMethodSymbol = semanticModel.GetSymbolInfo(argument.Expression).Symbol as IMethodSymbol;
					if (argMethodSymbol == null)
					{
						continue;
					}
					if (GetAsyncCounterparts(argMethodSymbol.OriginalDefinition, searchOptions, true).Any())
					{
						result.Add(argMethodSymbol);
					}
				}
			}
			return result;
		}

		private void Setup()
		{
			_configuration = ProjectData.Configuration.AnalyzeConfiguration;
			_solution = ProjectData.Project.Solution;
			_analyzeDocuments = ProjectData.Project.Documents
				.Where(o => _configuration.DocumentSelectionPredicate(o))
				.ToImmutableHashSet();
			_analyzeProjects = new[] { ProjectData.Project }
				.ToImmutableHashSet();
		}

		private void LogIgnoredReason(FunctionData functionData, bool warn = false)
		{
			var message = $"Method {functionData.Symbol} was ignored. Reason: {functionData.IgnoredReason}";
			if (warn)
			{
				Logger.Warn(message);
			}
			else
			{
				Logger.Debug(message);
			}
		}

		private void WarnLogIgnoredReason(FunctionData functionData)
		{
			LogIgnoredReason(functionData, true);
		}

		private void VoidLog(FunctionData functionData)
		{
		}
	}
}
