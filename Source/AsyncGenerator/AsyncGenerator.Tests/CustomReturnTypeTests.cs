﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AsyncGenerator.Analyzation;
using AsyncGenerator.Tests.TestCases;
using NUnit.Framework;

namespace AsyncGenerator.Tests
{
	public class CustomReturnTypeTests : BaseTest<CustomReturnType>
	{
		[Test]
		public void TestAfterAnalyzation()
		{
			var getData = GetMethodName(o => o.GetData());
			var getDataAsync = GetMethodName(o => o.GetDataAsync());

			var generator = new AsyncCodeGenerator();
			Action<IProjectAnalyzationResult> afterAnalyzationFn = result =>
			{
				Assert.AreEqual(1, result.Documents.Count);
				Assert.AreEqual(1, result.Documents[0].Namespaces.Count);
				Assert.AreEqual(1, result.Documents[0].Namespaces[0].Types.Count);
				Assert.AreEqual(2, result.Documents[0].Namespaces[0].Types[0].Methods.Count);

				var methods = result.Documents[0].Namespaces[0].Types[0].Methods.ToDictionary(o => o.Symbol.Name);

				Assert.AreEqual(MethodConversion.Ignore, methods[getData].Conversion);
				Assert.AreEqual(MethodConversion.Ignore, methods[getDataAsync].Conversion);
			};
			var config = Configure(p => p
				.ConfigureAnalyzation(a => a
					.MethodConversion(symbol =>
					{
						return symbol.Name == getData ?  MethodConversion.ToAsync : MethodConversion.Unknown;
					})
					.Callbacks(c => c
						.AfterAnalyzation(afterAnalyzationFn)
					)
				)
			);
			Assert.DoesNotThrowAsync(async () => await generator.GenerateAsync(config));
		}
	}
}
