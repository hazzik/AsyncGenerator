﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AsyncGenerator.Extensions;
using AsyncGenerator.Internal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

namespace AsyncGenerator.Analyzation
{
	public partial class ProjectAnalyzer
	{
		private async Task ScanDocumentData(DocumentData documentData)
		{
			foreach (var typeData in documentData.GetAllTypeDatas(o => o.Conversion != TypeConversion.Ignore))
			{
				// If the type have to be defined as a new type then we need to find all references to that type 
				if (typeData.Conversion == TypeConversion.NewType)
				{
					await ScanForTypeReferences(typeData).ConfigureAwait(false);
				}

				if (_configuration.ScanForMissingAsyncMembers)
				{
					ScanForTypeMissingAsyncMethods(typeData);
				}

				foreach (var methodData in typeData.MethodData.Values
					.Where(o => o.Conversion != MethodConversion.Ignore))
				{
					await ScanMethodData(methodData);
					foreach (var functionData in methodData.GetAllAnonymousFunctionData(o => o.Conversion != MethodConversion.Ignore))
					{
						// TODO: do we need something here?
					}
				}
			}
		}

		private async Task ScanMethodData(MethodData methodData, int depth = 0)
		{
			if (methodData.Scanned)
			{
				return;
			}
			methodData.Scanned = true;

			SyntaxReference syntax;
			var bodyScanMethodDatas = new HashSet<MethodData> { methodData };
			var referenceScanMethods = new HashSet<IMethodSymbol>();

			var interfaceMethods = methodData.ImplementedInterfaces.ToImmutableHashSet();
			if (methodData.InterfaceMethod)
			{
				interfaceMethods = interfaceMethods.Add(methodData.Symbol);
			}
			// Get and save all interface implementations
			foreach (var interfaceMethod in interfaceMethods)
			{
				referenceScanMethods.Add(interfaceMethod);

				syntax = interfaceMethod.DeclaringSyntaxReferences.Single();
				var document = _solution.GetDocument(syntax.SyntaxTree);
				if (!CanProcessDocument(document))
				{
					continue;
				}
				var documentData = ProjectData.GetDocumentData(document);
				var methodNode = (MethodDeclarationSyntax)await syntax.GetSyntaxAsync().ConfigureAwait(false);
				var interfaceMethodData = documentData.GetMethodData(methodNode);

				var implementations = await SymbolFinder.FindImplementationsAsync(
					interfaceMethod.OriginalDefinition, _solution, _analyzeProjects)
					.ConfigureAwait(false);
				foreach (var implementation in implementations.OfType<IMethodSymbol>())
				{
					syntax = implementation.DeclaringSyntaxReferences.Single();
					documentData = ProjectData.GetDocumentData(document);
					methodNode = (MethodDeclarationSyntax)await syntax.GetSyntaxAsync().ConfigureAwait(false);
					var implMethodData = documentData.GetMethodData(methodNode);

					interfaceMethodData.RelatedMethods.TryAdd(implMethodData);
					implMethodData.RelatedMethods.TryAdd(interfaceMethodData);

					if (_configuration.ScanMethodBody)
					{
						bodyScanMethodDatas.Add(implMethodData);
					}
				}
			}

			MethodData baseMethodData = null;
			IMethodSymbol baseMethodSymbol = null;
			if (methodData.BaseOverriddenMethod?.DeclaringSyntaxReferences.Any() == true)
			{
				baseMethodSymbol = methodData.BaseOverriddenMethod;
			}
			else if (methodData.Symbol.IsVirtual || methodData.Symbol.IsAbstract)
			{
				baseMethodSymbol = methodData.Symbol;
				baseMethodData = methodData;
			}

			// Get and save all derived methods
			if (baseMethodSymbol != null)
			{
				referenceScanMethods.Add(baseMethodSymbol);

				if (baseMethodData == null)
				{
					syntax = baseMethodSymbol.DeclaringSyntaxReferences.Single();
					var document = _solution.GetDocument(syntax.SyntaxTree);
					if (CanProcessDocument(document))
					{
						var methodNode = (MethodDeclarationSyntax)await syntax.GetSyntaxAsync().ConfigureAwait(false);
						baseMethodData = ProjectData.GetDocumentData(document).GetMethodData(methodNode);
					}
				}

				if (baseMethodData != null && _configuration.ScanMethodBody)
				{
					bodyScanMethodDatas.Add(baseMethodData);
				}

				var overrides = await SymbolFinder.FindOverridesAsync(baseMethodSymbol.OriginalDefinition,
					_solution, _analyzeProjects).ConfigureAwait(false);
				foreach (var overrideMethod in overrides.OfType<IMethodSymbol>())
				{
					syntax = overrideMethod.DeclaringSyntaxReferences.Single();
					var document = _solution.GetDocument(syntax.SyntaxTree);
					if (!CanProcessDocument(document))
					{
						continue;
					}
					var documentData = ProjectData.GetDocumentData(document);
					var methodNode = (MethodDeclarationSyntax)await syntax.GetSyntaxAsync().ConfigureAwait(false);
					var overrideMethodData = documentData.GetMethodData(methodNode);

					if (baseMethodData != null)
					{
						overrideMethodData.RelatedMethods.TryAdd(baseMethodData);
						baseMethodData.RelatedMethods.TryAdd(overrideMethodData);
					}
					else
					{
						overrideMethodData.ExternalRelatedMethods.TryAdd(baseMethodSymbol);
					}

					if (!overrideMethod.IsAbstract && _configuration.ScanMethodBody)
					{
						bodyScanMethodDatas.Add(overrideMethodData);
					}
				}
			}

			if (baseMethodSymbol == null && !interfaceMethods.Any()) //TODO: what about hiding methods
			{
				referenceScanMethods.Add(methodData.Symbol);
			}

			if (_configuration.ScanMethodBody ||
				methodData.Conversion == MethodConversion.Smart ||
				methodData.Conversion == MethodConversion.ToAsync)
			{
				foreach (var mData in bodyScanMethodDatas)
				{
					foreach (var method in FindNewlyInvokedMethodsWithAsyncCounterpart(mData))
					{
						await ScanAllMethodReferenceLocations(method, depth).ConfigureAwait(false);
					}
				}
			}
			foreach (var methodToScan in referenceScanMethods)
			{
				await ScanAllMethodReferenceLocations(methodToScan, depth).ConfigureAwait(false);
			}
		}

		/// <summary>
		/// When a type needs to be defined as a new type we need to find all references to them.
		/// Reference can point to a variable, field, base type, argument definition
		/// </summary>
		private async Task ScanForTypeReferences(TypeData typeData)
		{
			// References for ctor of the type and the type itself wont have any locations
			var references = await SymbolFinder.FindReferencesAsync(typeData.Symbol, _solution, _analyzeDocuments).ConfigureAwait(false);
			foreach (var refLocation in references.SelectMany(o => o.Locations))
			{
				var documentData = ProjectData.GetDocumentData(refLocation.Document);
				if (!typeData.SelfReferences.TryAdd(refLocation))
				{
					continue; // Reference already processed
				}

				// We need to find the type where the reference location is
				var node = documentData.Node.DescendantNodes(descendIntoTrivia: true)
					.First(
						o =>
						{
							if (o.IsKind(SyntaxKind.GenericName))
							{
								return o.ChildTokens().First(t => t.IsKind(SyntaxKind.IdentifierToken)).Span ==
									   refLocation.Location.SourceSpan;
							}
							return o.Span == refLocation.Location.SourceSpan;
						});

				var funcNode = node.Ancestors().OfType<AnonymousFunctionExpressionSyntax>().FirstOrDefault();
				if (funcNode != null)
				{
					var functionData = documentData.GetAnonymousFunctionData(funcNode);
					functionData.TypeReferences.TryAdd(refLocation);
					continue;
				}

				var methodNode = node.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
				if (methodNode != null)
				{
					var methodData = documentData.GetMethodData(methodNode);
					methodData.TypeReferences.TryAdd(refLocation);
					continue;
				}
				var type = node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
				if (type != null)
				{
					var refTypeData = documentData.GetTypeData(type);
					refTypeData.TypeReferences.TryAdd(refLocation);
					continue;
				}
				// Can happen when declaring a Name in a using statement
				var namespaceNode = node.Ancestors().OfType<NamespaceDeclarationSyntax>().FirstOrDefault();
				var namespaceData = documentData.GetNamespaceData(namespaceNode);
				namespaceData.TypeReferences.TryAdd(refLocation);
			}
		}

		private void ScanForTypeMissingAsyncMethods(TypeData typeData)
		{
			var documentData = typeData.NamespaceData.DocumentData;
			var members = typeData.Node
				.DescendantNodes()
				.OfType<MethodDeclarationSyntax>()
				.Select(o => new { Node = o, Symbol = documentData.SemanticModel.GetDeclaredSymbol(o) })
				.ToLookup(o =>
					o.Symbol.MethodKind == MethodKind.ExplicitInterfaceImplementation
						? o.Symbol.Name.Split('.').Last()
						: o.Symbol.Name);
			//var methodDatas = new List<MethodData>();

			foreach (var asyncMember in typeData.Symbol.AllInterfaces
												  .SelectMany(o => o.GetMembers().OfType<IMethodSymbol>()
												  .Where(m => m.Name.EndsWith("Async"))))
			{
				// Skip if there is already an implementation defined
				var impl = typeData.Symbol.FindImplementationForInterfaceMember(asyncMember);
				if (impl != null)
				{
					continue;
				}
				var nonAsyncName = asyncMember.Name.Remove(asyncMember.Name.LastIndexOf("Async", StringComparison.InvariantCulture));
				if (!members.Contains(nonAsyncName))
				{
					Logger.Debug($"Sync counterpart of async member {asyncMember} not found in file {documentData.FilePath}");
					continue;
				}
				var nonAsyncMember = members[nonAsyncName].First(o => o.Symbol.IsAsyncCounterpart(asyncMember, true, false, false));
				var methodData = documentData.GetMethodData(nonAsyncMember.Node);
				methodData.Conversion = MethodConversion.ToAsync;
				//methodDatas.Add(methodData);
			}

			// Find all abstract non implemented async methods. Descend base types until we find a non abstract one.
			var baseType = typeData.Symbol.BaseType;
			while (baseType != null)
			{
				if (!baseType.IsAbstract)
				{
					break;
				}
				foreach (var asyncMember in baseType.GetMembers()
					.OfType<IMethodSymbol>()
					.Where(o => o.IsAbstract && o.Name.EndsWith("Async")))
				{
					var nonAsyncName = asyncMember.Name.Remove(asyncMember.Name.LastIndexOf("Async", StringComparison.InvariantCulture));
					if (!members.Contains(nonAsyncName))
					{
						Logger.Debug($"Abstract sync counterpart of async member {asyncMember} not found in file {documentData.FilePath}");
						continue;
					}
					var nonAsyncMember = members[nonAsyncName].FirstOrDefault(o => o.Symbol.IsAsyncCounterpart(asyncMember, true, false, false));
					if (nonAsyncMember == null)
					{
						Logger.Debug($"Abstract sync counterpart of async member {asyncMember} not found in file {documentData.FilePath}");
						continue;
					}
					var methodData = documentData.GetMethodData(nonAsyncMember.Node);
					methodData.Conversion = MethodConversion.ToAsync;
					//methodDatas.Add(methodData);
				}
				baseType = baseType.BaseType;
			}
		}

		#region ScanAllMethodReferenceLocations

		private readonly ConcurrentSet<IMethodSymbol> _scannedMethodReferenceSymbols = new ConcurrentSet<IMethodSymbol>();

		private async Task ScanAllMethodReferenceLocations(IMethodSymbol methodSymbol, int depth)
		{
			if (_scannedMethodReferenceSymbols.Contains(methodSymbol.OriginalDefinition))
			{
				return;
			}
			_scannedMethodReferenceSymbols.TryAdd(methodSymbol.OriginalDefinition);

			var references = await SymbolFinder.FindReferencesAsync(methodSymbol.OriginalDefinition,
				_solution, _analyzeDocuments).ConfigureAwait(false);

			depth++;
			foreach (var refLocation in references.SelectMany(o => o.Locations))
			{
				if (refLocation.Document.Project != ProjectData.Project)
				{
					throw new InvalidOperationException($"Reference {refLocation} is located in a document from another project");
				}

				var documentData = ProjectData.GetDocumentData(refLocation.Document);
				if (documentData == null)
				{
					continue;
				}
				var symbol = documentData.GetEnclosingSymbol(refLocation);
				if (symbol == null)
				{
					Logger.Debug($"Symbol not found for reference ${refLocation}");
					continue;
				}

				var refMethodSymbol = symbol as IMethodSymbol;
				if (refMethodSymbol == null)
				{
					continue;
				}
				var baseMethodData = await documentData.GetAnonymousFunctionOrMethodData(refMethodSymbol).ConfigureAwait(false);
				if (baseMethodData == null)
				{
					continue;
				}
				// Save the reference as it can be made async
				if (!baseMethodData.MethodReferences.TryAdd(refLocation))
				{
					continue; // Reference already processed
				}

				// Find the real method on that reference as FindReferencesAsync will also find references to base and interface methods
				var nameNode = baseMethodData.GetNode().DescendantNodes()
							   .OfType<SimpleNameSyntax>()
							   .First(
								   o =>
								   {
									   if (o.IsKind(SyntaxKind.GenericName))
									   {
										   return o.ChildTokens().First(t => t.IsKind(SyntaxKind.IdentifierToken)).Span ==
												  refLocation.Location.SourceSpan;
									   }
									   return o.Span == refLocation.Location.SourceSpan;
								   });
				var invokedSymbol = (IMethodSymbol)documentData.SemanticModel.GetSymbolInfo(nameNode).Symbol;

				var invokedMethodData = await ProjectData.GetMethodData(invokedSymbol).ConfigureAwait(false);
				invokedMethodData?.InvokedBy.Add(baseMethodData);

				var methodData = baseMethodData as MethodData;
				if (methodData != null)
				{
					await ScanMethodData(methodData, depth).ConfigureAwait(false);
				}
			}
		}

		#endregion
	}
}
