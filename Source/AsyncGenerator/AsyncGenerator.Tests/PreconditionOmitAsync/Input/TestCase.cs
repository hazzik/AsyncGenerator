﻿using System;
using AsyncGenerator.TestCases;

namespace AsyncGenerator.Tests.PreconditionOmitAsync.Input
{
	public class TestCase
	{
		public string PreconditionReturn(string name)
		{
			if (name == null)
			{
				throw new ArgumentNullException(nameof(name));
			}
			return ReadFile();
		}

		public void PreconditionVoid(string name)
		{
			if (name == null)
			{
				throw new ArgumentNullException(nameof(name));
			}
			SimpleFile.Read();
		}

		public string PreconditionToSplit(string name)
		{
			if (name == null)
			{
				throw new ArgumentNullException(nameof(name));
			}
			SimpleFile.Read();
			return "";
		}

		public string SyncPrecondition(string name)
		{
			if (name == null)
			{
				throw new ArgumentNullException(nameof(name));
			}
			return SyncReadFile();
		}

		public string ReadFile()
		{
			SimpleFile.Read();
			return "";
		}

		public string SyncReadFile()
		{
			return "";
		}
	}
}