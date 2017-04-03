﻿using System;
using Microsoft.CodeAnalysis;

namespace AsyncGenerator.Configuration
{
	public interface IProjectAnalyzeConfiguration
	{
		/// <summary>
		/// Set a function that will decide what type of conversion to apply for a given method
		/// </summary>
		IProjectAnalyzeConfiguration MethodConversionFunction(Func<IMethodSymbol, MethodConversion> func);

		/// <summary>
		/// Set a function that will decide what type of conversion to apply for a given type
		/// </summary>
		IProjectAnalyzeConfiguration TypeConversionFunction(Func<INamedTypeSymbol, TypeConversion> func);

		/// <summary>
		/// Set a predicate that will decide if the document will be analyzed
		/// </summary>
		IProjectAnalyzeConfiguration DocumentSelectionPredicate(Predicate<Document> predicate);

		/// <summary>
		/// Set a predicate that will decide if the method will be analyzed
		/// </summary>
		//IProjectAnalyzeConfiguration MethodSelectionPredicate(Predicate<IMethodSymbol> predicate);

		/// <summary>
		/// Set a predicate that will decide if the type will be analyzed
		/// </summary>
		//IProjectAnalyzeConfiguration TypeSelectionPredicate(Predicate<INamedTypeSymbol> predicate);

		/// <summary>
		/// Set a predicate that will decide if the method that can be converted to async should be converted
		/// </summary>
		IProjectAnalyzeConfiguration ConvertMethodPredicate(Predicate<IMethodSymbol> predicate);

		/// <summary>
		/// Append a function that will try to find an async counterpart for the given method
		/// </summary>
		IProjectAnalyzeConfiguration AppendFindAsyncCounterpartDelegate(FindAsyncCounterpart func);

		/// <summary>
		/// Prepend a function that will try to find an async counterpart for the given method
		/// </summary>
		IProjectAnalyzeConfiguration PrependFindAsyncCounterpartDelegate(FindAsyncCounterpart func);

		/// <summary>
		/// Enable or disable scanning for async counterparts within a method body
		/// </summary>
		IProjectAnalyzeConfiguration ScanMethodBody(bool value);

		/// <summary>
		/// Enable or disable scanning for missing async counterparts
		/// </summary>
		IProjectAnalyzeConfiguration ScanForMissingAsyncMembers(bool value);

		/// <summary>
		/// Callbacks that will be called in certain parts of the analyzation process
		/// </summary>
		IProjectAnalyzeConfiguration Callbacks(Action<IProjectAnalyzeCallbacksConfiguration> action);

	}
}