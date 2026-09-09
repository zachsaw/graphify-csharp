#!/usr/bin/env dotnet
#:sdk Microsoft.NET.Sdk
#:property TargetFramework=net10.0
#:package Humanizer.Core@2.14.1
#:project ../LanguageSurfaceFixture/LanguageSurfaceFixture.csproj

using Humanizer;

System.Console.WriteLine("probe".Pascalize());
