﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AsyncGenerator.Analyzation;

namespace AsyncGenerator.Transformation
{
	public interface IFunctionReferenceTransformationResult : ITransformationResult
	{
		IFunctionReferenceAnalyzationResult AnalyzationResult { get; }
	}
}
