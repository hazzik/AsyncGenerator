﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AsyncGenerator.Configuration;
using Microsoft.CodeAnalysis;

namespace AsyncGenerator.Plugins
{
	public interface IPlugin
	{
		Task Initialize(Project project, IProjectConfiguration configuration);
	}
}
