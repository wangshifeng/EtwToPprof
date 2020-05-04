﻿using Google.Protobuf;
using pb = Google.Pprof.Protobuf;

using Microsoft.Windows.EventTracing;
using Microsoft.Windows.EventTracing.Cpu;
using Microsoft.Windows.EventTracing.Processes;
using Microsoft.Windows.EventTracing.Symbols;

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace EtwToPprof
{
  class ProfileWriter
  {
    public ProfileWriter(string etlFileName,
                         bool includeInlinedFunctions,
                         string stripSourceFileNamePrefix)
    {
      this.includeInlinedFunctions = includeInlinedFunctions;

      stripSourceFileNamePrefixRegex = new Regex(stripSourceFileNamePrefix,
                                                 RegexOptions.Compiled | RegexOptions.IgnoreCase);

      profile = new pb.Profile();
      profile.StringTable.Add("");
      strings = new Dictionary<string, long>();
      strings.Add("", 0);
      nextStringId = 1;

      var cpuTimeValueType = new pb.ValueType();
      cpuTimeValueType.Type = GetStringId("cpu");
      cpuTimeValueType.Unit = GetStringId("nanoseconds");
      profile.SampleType.Add(cpuTimeValueType);

      profile.Comment.Add(
        GetStringId(String.Format("Converted by EtwToPprof from {0}", Path.GetFileName(etlFileName))));

      locations = new Dictionary<Location, ulong>();
      nextLocationId = 1;

      functions = new Dictionary<Function, ulong>();
      nextFunctionId = 1;
    }

    public void AddSample(ICpuSample sample)
    {
      var sampleProto = new pb.Sample();
      sampleProto.Value.Add(sample.Weight.Nanoseconds);
      if (sample.Stack != null)
      {
        foreach (var stackFrame in sample.Stack.Frames)
        {
          if (stackFrame.HasValue)
          {
            sampleProto.LocationId.Add(
              GetLocationId(sample.Process, stackFrame.Address, stackFrame.Symbol));
          }
        }
        string threadName = sample.Thread?.Name;
        if (threadName == "" || threadName == null)
        {
          threadName = String.Format("thread ({0})", sample.Thread?.Id ?? 0);
        }
        sampleProto.LocationId.Add(
          GetLocationId(sample.Process, sample.Thread.StartAddress, null, threadName));

        string processName = String.Format("{0} ({1})", sample.Process.ImageName, sample.Process.Id);
        sampleProto.LocationId.Add(
          GetLocationId(sample.Process, sample.Process.ObjectAddress, null, processName));
      }

      profile.Sample.Add(sampleProto);
    }

    public long Write(string outputFileName)
    {
      using (FileStream output = File.Create(outputFileName))
      {
        using (GZipStream gzip = new GZipStream(output, CompressionMode.Compress))
        {
          using (CodedOutputStream serialized = new CodedOutputStream(gzip))
          {
            profile.WriteTo(serialized);
            return output.Length;
          }
        }
      }
    }

    readonly struct Location
    {
      public Location(int processId, Address address, string imageName, string functionName)
      {
        ProcessId = processId;
        Address = address;
        ImageName = imageName;
        FunctionName = functionName;
      }
      int ProcessId { get; }
      Address Address { get; }
      string ImageName { get; }
      string FunctionName { get; }

      public override bool Equals(object other)
      {
        return other is Location l
               && l.ProcessId == ProcessId
               && l.Address == Address
               && l.ImageName == ImageName
               && l.FunctionName == FunctionName;
      }

      public override int GetHashCode()
      {
        return (ProcessId, Address, ImageName, FunctionName).GetHashCode();
      }
    }

    ulong GetLocationId(IProcess process,
                        Address address,
                        IStackSymbol stackSymbol,
                        string pseudoFunctionName = null)    {
      ulong locationId;
      var imageName = stackSymbol?.Image.FileName;
      if (imageName == null)
        imageName = process.ImageName;

      var functionName = stackSymbol?.FunctionName;
      if (functionName == null)
        functionName = pseudoFunctionName;

      var location = new Location(process.Id, address, imageName, functionName);
      if (!locations.TryGetValue(location, out locationId))
      {
        var locationProto = new pb.Location();
        locationProto.Id = nextLocationId++;

        pb.Line line;
        if (includeInlinedFunctions && stackSymbol?.InlinedFunctionNames != null)
        {
          foreach (var inlineFunctionName in stackSymbol.InlinedFunctionNames)
          {
              line = new pb.Line();
              line.FunctionId = GetFunctionId(imageName, inlineFunctionName);
              locationProto.Line.Add(line);
          }
        }
        line = new pb.Line();
        line.FunctionId = GetFunctionId(imageName, functionName, stackSymbol?.SourceFileName);
        line.Line_ = stackSymbol?.SourceLineNumber ?? 0;
        locationProto.Line.Add(line);

        locationId = locationProto.Id;
        locations.Add(location, locationId);
        profile.Location.Add(locationProto);
      }
      return locationId;
    }

    readonly struct Function
    {
      public Function(string imageName, string functionName)
      {
        ImageName = imageName;
        FunctionName = functionName;
      }
      string ImageName { get; }
      string FunctionName { get; }

      public override bool Equals(object other)
      {
        return other is Function f && f.ImageName == ImageName && f.FunctionName == FunctionName;
      }

      public override int GetHashCode()
      {
        return (ImageName, FunctionName).GetHashCode();
      }

      public override string ToString()
      {
        return String.Format("{0}!{1}", ImageName, FunctionName);
      }
    }

    ulong GetFunctionId(string imageName, string functionName, string sourceFileName = null)
    {
      ulong functionId;
      var function = new Function(imageName, functionName);
      if (!functions.TryGetValue(function, out functionId))
      {
        var functionProto = new pb.Function();
        functionProto.Id = nextFunctionId++;
        functionProto.Name = GetStringId(functionName ?? function.ToString());
        functionProto.SystemName = GetStringId(function.ToString());
        if (sourceFileName == null)
        {
          sourceFileName = imageName;
        }
        else
        {
          sourceFileName = sourceFileName.Replace('\\', '/');
          sourceFileName = stripSourceFileNamePrefixRegex.Replace(sourceFileName, "");
        }
        functionProto.Filename = GetStringId(sourceFileName);

        functionId = functionProto.Id;
        functions.Add(function, functionId);
        profile.Function.Add(functionProto);
      }
      return functionId;
    }

    long GetStringId(string str)
    {
      long stringId;
      if (!strings.TryGetValue(str, out stringId))
      {
        stringId = nextStringId++;
        strings.Add(str, stringId);
        profile.StringTable.Add(str);
      }
      return stringId;
    }

    Dictionary<Location, ulong> locations;
    ulong nextLocationId;

    Dictionary<Function, ulong> functions;
    ulong nextFunctionId;

    Dictionary<string, long> strings;
    long nextStringId;

    bool includeInlinedFunctions;
    Regex stripSourceFileNamePrefixRegex;

    pb.Profile profile;
  }
}