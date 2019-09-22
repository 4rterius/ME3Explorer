﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Gammtek.Conduit.Extensions.Collections.Generic;
using ME3Explorer.Debugging;
using ME3Explorer.Packages;
using ME3Explorer.SharedUI;
using ME3Explorer.Unreal;
using ME3Explorer.Unreal.BinaryConverters;

namespace ME3Explorer
{
    public static class Relinker
    {
        /// <summary>
        /// Attempts to relink unreal property data and object pointers in binary when cross porting an export
        /// </summary>
        public static List<string> RelinkAll(Dictionary<IEntry, IEntry> crossPccObjectMap, IMEPackage importpcc)
        {
            List<string> results = RelinkProperties(crossPccObjectMap, importpcc);
            results.AddRange(RelinkBinaryObjects(crossPccObjectMap, importpcc));
            return results;
        }

        /// <summary>
        /// Attempts to relink unreal property data using propertycollection when cross porting an export
        /// </summary>
        public static List<string> RelinkProperties(Dictionary<IEntry, IEntry> crossPccObjectMap, IMEPackage importpcc)
        {
            var relinkResults = new List<string>();
            //relink each modified export

            //We must convert this to a list, as this list will be updated as imports are cross mapped during relinking.
            //This process speeds up same-relinks later.
            //This is a list because otherwise we would get a concurrent modification exception.
            //Since we only enumerate exports and append imports to this list we will not need to worry about recursive links
            //I am sure this won't come back to be a pain for me.
            var crossPCCObjectMappingList = new OrderedMultiValueDictionary<IEntry, IEntry>(crossPccObjectMap);
            //can't be a foreach since we might append things to the list
            // ReSharper disable once ForCanBeConvertedToForeach
            for (int i = 0; i < crossPCCObjectMappingList.Count; i++)
            {
                KeyValuePair<IEntry, IEntry> kvp = crossPCCObjectMappingList[i];
                if (kvp.Key is ExportEntry sourceExportInOriginalFile)
                {
                    ExportEntry Value = (ExportEntry)kvp.Value;
                    PropertyCollection transplantProps = sourceExportInOriginalFile.GetProperties();
                    Debug.WriteLine($"Relinking items in destination export: {sourceExportInOriginalFile.FullPath}");
                    relinkResults.AddRange(relinkPropertiesRecursive(importpcc, Value, transplantProps, crossPCCObjectMappingList, ""));
                    Value.WriteProperties(transplantProps);
                }
            }

            crossPccObjectMap.Clear();
            crossPccObjectMap.AddRange(crossPCCObjectMappingList);
            return relinkResults;
        }

        /// <summary>
        /// Attempts to relink unreal binary data to object pointers if they are part of the clone tree.
        /// It's gonna be an ugly mess.
        /// </summary>
        /// <param name="crossPccObjectMap"></param>
        /// <param name="importpcc">PCC being imported from</param>
        public static List<string> RelinkBinaryObjects(Dictionary<IEntry, IEntry> crossPccObjectMap, IMEPackage importpcc)
        {
            var relinkFailedReport = new List<string>();
            var crossPCCObjectMappingList = new OrderedMultiValueDictionary<IEntry, IEntry>(crossPccObjectMap);
            //can't be a foreach since we might append things to the list
            // ReSharper disable once ForCanBeConvertedToForeach
            for (int index = 0; index < crossPCCObjectMappingList.Count; index++)
            {
                KeyValuePair<IEntry, IEntry> mapping = crossPCCObjectMappingList[index];
                if (mapping.Key is ExportEntry sourceexp)
                {
                    ExportEntry exp = (ExportEntry)mapping.Value;
                    try
                    {
                        if (ObjectBinary.From(exp) is ObjectBinary objBin)
                        {
                            List<(UIndex, string)> indices = objBin.GetUIndexes(exp.FileRef.Game);

                            foreach ((UIndex uIndex, string propName) in indices)
                            {
                                string result = relinkObjectProperty(importpcc, exp, ref uIndex.value, $"(Binary Property: {propName})", crossPCCObjectMappingList, "");
                                if (result != null)
                                {
                                    relinkFailedReport.Add(result);
                                }
                            }

                            exp.setBinaryData(objBin.ToBytes(exp.FileRef));
                            continue;
                        }

                        byte[] binarydata = exp.getBinaryData();
                        if (binarydata.Length > 0)
                        {
                            switch (exp.ClassName)
                            {
                                case "WwiseEvent":
                                {
                                    if (exp.FileRef.Game == MEGame.ME3)
                                    {
                                        int count = BitConverter.ToInt32(binarydata, 0);
                                        for (int i = 0; i < count; i++)
                                        {
                                            int originalValue = BitConverter.ToInt32(binarydata, 4 + (i * 4));

                                            //This might throw an exception if it was invalid in the original file...
                                            bool isMapped = crossPCCObjectMappingList.TryGetValue(importpcc.GetEntry(originalValue), out IEntry mappedValueInThisPackage);
                                            if (isMapped)
                                            {
                                                Debug.WriteLine("Binary relink hit for ME3 WwiseEvent Export " + exp.UIndex + " 0x" + (4 + (i * 4)).ToString("X6") + " " + originalValue + " -> " + (mappedValueInThisPackage.UIndex));
                                                binarydata.OverwriteRange(4 + (i * 4), BitConverter.GetBytes(mappedValueInThisPackage.UIndex));
                                                int newValue = BitConverter.ToInt32(binarydata, 4 + (i * 4));
                                                Debug.WriteLine(originalValue + " -> " + newValue);
                                            }
                                            else
                                            {
                                                Debug.WriteLine("Binary relink missed ME3 WwiseEvent Export " + exp.UIndex + " 0x" + (4 + (i * 4)).ToString("X6") + " " + originalValue);
                                                relinkFailedReport.Add(exp.UIndex + " " + exp.FullPath + " binary relink error: WwiseEvent referenced WwiseStream " + originalValue + " is not in the mapping tree and could not be relinked");
                                            }
                                        }

                                        exp.setBinaryData(binarydata);
                                    }
                                    else if (exp.FileRef.Game == MEGame.ME2)
                                    {
                                        int parsingPos = 4;
                                        int linkCount = BitConverter.ToInt32(binarydata, parsingPos);
                                        parsingPos += 4;
                                        for (int i = 0; i < linkCount; i++)
                                        {
                                            int bankcount = BitConverter.ToInt32(binarydata, parsingPos);
                                            parsingPos += 4;
                                            for (int j = 0; j < bankcount; j++)
                                            {
                                                int bankRef = BitConverter.ToInt32(binarydata, parsingPos);
                                                bool isMapped = crossPCCObjectMappingList.TryGetValue(importpcc.GetEntry(bankRef), out IEntry mappedValueInThisPackage);
                                                if (isMapped)
                                                {
                                                    Debug.WriteLine("Binary relink hit for ME2 WwiseEvent Bank Entry " + exp.UIndex + " 0x" + (4 + (i * 4)).ToString("X6") + " " + bankRef + " -> " + (mappedValueInThisPackage.UIndex));
                                                    binarydata.OverwriteRange(parsingPos, BitConverter.GetBytes(mappedValueInThisPackage.UIndex));
                                                    int newValue = BitConverter.ToInt32(binarydata, 4 + (i * 4));
                                                    Debug.WriteLine(bankRef + " -> " + newValue);
                                                }
                                                else
                                                {
                                                    Debug.WriteLine("Binary relink missed ME2 WwiseEvent Bank Entry " + exp.UIndex + " 0x" + (4 + (i * 4)).ToString("X6") + " " + bankRef);
                                                    relinkFailedReport.Add(exp.UIndex + " " + exp.InstancedFullPath + " binary relink error: ME2 WwiseEvent referenced WwiseBank " + bankRef + " is not in the mapping tree and could not be relinked");
                                                }

                                                parsingPos += 4;
                                            }

                                            int wwisestreamcount = BitConverter.ToInt32(binarydata, parsingPos);
                                            parsingPos += 4;
                                            for (int j = 0; j < wwisestreamcount; j++)
                                            {
                                                int wwiseStreamRef = BitConverter.ToInt32(binarydata, parsingPos);
                                                bool isMapped = crossPCCObjectMappingList.TryGetValue(importpcc.GetEntry(wwiseStreamRef), out IEntry mappedValueInThisPackage);
                                                if (isMapped)
                                                {
                                                    Debug.WriteLine("Binary relink hit for ME2 WwiseEvent WwiseStream Entry " + exp.UIndex + " 0x" + (4 + (i * 4)).ToString("X6") + " " + wwiseStreamRef + " -> " + (mappedValueInThisPackage.UIndex));
                                                    binarydata.OverwriteRange(parsingPos, BitConverter.GetBytes(mappedValueInThisPackage.UIndex));
                                                    int newValue = BitConverter.ToInt32(binarydata, 4 + (i * 4));
                                                    Debug.WriteLine(wwiseStreamRef + " -> " + newValue);
                                                }
                                                else
                                                {
                                                    Debug.WriteLine("Binary relink missed ME2 WwiseEvent Bank Entry " + exp.UIndex + " 0x" + (4 + (i * 4)).ToString("X6") + " " + wwiseStreamRef);
                                                    relinkFailedReport.Add(exp.UIndex + " " + exp.InstancedFullPath + " binary relink error: ME2 WwiseEvent referenced WwiseStream " + wwiseStreamRef + " is not in the mapping tree and could not be relinked");
                                                }

                                                parsingPos += 4;
                                            }
                                        }

                                        exp.setBinaryData(binarydata);
                                    }
                                }
                                    break;
                                case "Class":
                                {
                                    if (exp.FileRef.Game != importpcc.Game)
                                    {
                                        //Cannot relink against a different game.
                                        continue;
                                    }

                                    ExportEntry importingExp = sourceexp;
                                    if (importingExp.ClassName != "Class")
                                    {
                                        continue; //the class was not actually set, so this is not really class.
                                    }

                                    //This is going to be pretty ugly
                                    try
                                    {
                                        byte[] newdata = sourceexp.Data; //may need to rewrite first unreal header
                                        byte[] data = sourceexp.Data;

                                        int offset = 0;
                                        int unrealExportIndex = BitConverter.ToInt32(data, offset);
                                        offset += 4;


                                        int superclassIndex = BitConverter.ToInt32(data, offset);
                                        if (superclassIndex < 0)
                                        {
                                            //its an import
                                            ImportEntry superclassImportEntry = importpcc.GetImport(superclassIndex);
                                            IEntry newSuperclassValue = EntryImporter.getOrAddCrossImportOrPackage(superclassImportEntry.FullPath, importpcc, exp.FileRef);
                                            newdata.OverwriteRange(offset, BitConverter.GetBytes(newSuperclassValue.UIndex));
                                        }
                                        else
                                        {
                                            relinkFailedReport.Add(exp.UIndex + " " + exp.FullPath + " binary relink error: Superclass is an export in the source package and was not relinked.");
                                        }

                                        offset += 4;
                                        int unknown1 = BitConverter.ToInt32(data, offset);

                                        offset += 4;
                                        int childProbeUIndex = BitConverter.ToInt32(data, offset);
                                        if (childProbeUIndex != 0)
                                        { //Scoped
                                            if (crossPCCObjectMappingList.TryGetValue(importpcc.GetEntry(childProbeUIndex), out IEntry mapped))
                                            {
                                                newdata.OverwriteRange(offset, BitConverter.GetBytes(mapped.UIndex));
                                            }
                                            else
                                            {
                                                relinkFailedReport.Add(exp.UIndex + " " + exp.FullPath + " binary relink error: Child Probe UIndex could not be remapped during porting: " + childProbeUIndex + " is not in the mapping tree and could not be relinked");
                                            }
                                        }

                                        offset += 4;


                                        //I am not sure what these mean. However if Pt1&2 are 33/25, the following bytes that follow are extended.
                                        int headerUnknown1 = BitConverter.ToInt32(data, offset);
                                        Int64 ignoreMask = BitConverter.ToInt64(data, offset);
                                        offset += 8;

                                        Int16 labelOffset = BitConverter.ToInt16(data, offset);
                                        offset += 2;
                                        int skipAmount = 0x6;
                                        //Find end of script block. Seems to be 10 FF's.
                                        while (offset + skipAmount + 10 < data.Length)
                                        {
                                            //Debug.WriteLine("Cheecking at 0x"+(offset + skipAmount + 10).ToString("X4"));
                                            bool isEnd = true;
                                            for (int i = 0; i < 10; i++)
                                            {
                                                byte b = data[offset + skipAmount + i];
                                                if (b != 0xFF)
                                                {
                                                    isEnd = false;
                                                    break;
                                                }
                                            }

                                            if (isEnd)
                                            {
                                                break;
                                            }
                                            else
                                            {
                                                skipAmount++;
                                            }
                                        }

                                        offset += skipAmount + 10; //heuristic to find end of script (ends with 10 FF's)
                                        uint stateMask = BitConverter.ToUInt32(data, offset);
                                        offset += 4;

                                        int localFunctionsTableCount = BitConverter.ToInt32(data, offset);
                                        offset += 4;
                                        bool isMapped;
                                        for (int i = 0; i < localFunctionsTableCount; i++)
                                        {
                                            int nameTableIndex = BitConverter.ToInt32(data, offset);
                                            int nameIndex = BitConverter.ToInt32(data, offset + 4);
                                            NameReference importingName = importpcc.GetNameEntry(nameTableIndex);
                                            int newFuncName = exp.FileRef.FindNameOrAdd(importingName);
                                            newdata.OverwriteRange(offset, BitConverter.GetBytes(newFuncName)); //Need to convert to SirC way of doing it
                                            offset += 8;

                                            int functionObjectIndex = BitConverter.ToInt32(data, offset);

                                            //TODO: Add lookup
                                            if (crossPCCObjectMappingList.TryGetValue(importpcc.GetEntry(functionObjectIndex), out IEntry mapped))
                                            {
                                                newdata.OverwriteRange(offset, BitConverter.GetBytes(mapped.UIndex));
                                            }
                                            else
                                            {
                                                relinkFailedReport.Add(exp.UIndex + " " + exp.FullPath + " binary relink error: Local function[" + i + "] could not be remapped during porting: " + functionObjectIndex + " is not in the mapping tree and could not be relinked");
                                            }

                                            offset += 4;
                                        }

                                        int classMask = BitConverter.ToInt32(data, offset);
                                        offset += 4;
                                        if (importpcc.Game != MEGame.ME3)
                                        {
                                            offset += 1; //seems to be a blank byte here
                                        }

                                        int coreReference = BitConverter.ToInt32(data, offset);
                                        if (coreReference < 0)
                                        {
                                            //its an import
                                            ImportEntry outerclassReferenceImport = importpcc.GetImport(coreReference);
                                            IEntry outerclassNewImport = EntryImporter.getOrAddCrossImportOrPackage(outerclassReferenceImport.FullPath, importpcc, exp.FileRef);
                                            newdata.OverwriteRange(offset, BitConverter.GetBytes(outerclassNewImport.UIndex));
                                        }
                                        else
                                        {
                                            relinkFailedReport.Add(exp.UIndex + " " + exp.FullPath + " binary relink error: Outerclass is an export in the original package, not relinked.");
                                        }

                                        offset += 4;


                                        if (importpcc.Game == MEGame.ME3)
                                        {
                                            offset = ClassParser_RelinkComponentsTable(importpcc, exp, relinkFailedReport, ref newdata, offset, crossPccObjectMap);
                                            offset = ClassParser_ReadImplementsTable(importpcc, exp, relinkFailedReport, ref newdata, offset, crossPccObjectMap);
                                            int postComponentsNoneNameIndex = BitConverter.ToInt32(data, offset);
                                            int postComponentNoneIndex = BitConverter.ToInt32(data, offset + 4);
                                            string postCompName = importpcc.GetNameEntry(postComponentsNoneNameIndex); //This appears to be unused in ME3, it is always None it seems.
                                            int newFuncName = exp.FileRef.FindNameOrAdd(postCompName);
                                            newdata.OverwriteRange(offset, BitConverter.GetBytes(newFuncName));
                                            offset += 8;

                                            int unknown4 = BitConverter.ToInt32(data, offset);
                                            offset += 4;
                                        }
                                        else
                                        {
                                            offset = ClassParser_ReadImplementsTable(importpcc, exp, relinkFailedReport, ref data, offset, crossPccObjectMap);
                                            offset = ClassParser_RelinkComponentsTable(importpcc, exp, relinkFailedReport, ref data, offset, crossPccObjectMap);

                                            int me12unknownend1 = BitConverter.ToInt32(data, offset);
                                            offset += 4;

                                            int me12unknownend2 = BitConverter.ToInt32(data, offset);
                                            offset += 4;
                                        }

                                        int defaultsClassLink = BitConverter.ToInt32(data, offset);

                                        isMapped = crossPCCObjectMappingList.TryGetValue(importpcc.GetEntry(defaultsClassLink), out IEntry defClassLink);
                                        if (isMapped)
                                        {
                                            newdata.OverwriteRange(offset, BitConverter.GetBytes(defClassLink.UIndex));
                                        }
                                        else
                                        {
                                            relinkFailedReport.Add(exp.UIndex + " " + exp.FullPath + " binary relink error: DefaultsClassLink cannot be currently automatically relinked by Binary Relinker. Please manually set this in Binary Editor");
                                        }

                                        offset += 4;

                                        if (importpcc.Game == MEGame.ME3)
                                        {
                                            int functionsTableCount = BitConverter.ToInt32(data, offset);
                                            offset += 4;

                                            for (int i = 0; i < functionsTableCount; i++)
                                            {
                                                int functionsTableIndex = BitConverter.ToInt32(data, offset);
                                                if (crossPCCObjectMappingList.TryGetValue(importpcc.GetEntry(functionsTableIndex), out IEntry mapped))
                                                {
                                                    newdata.OverwriteRange(offset, BitConverter.GetBytes(mapped.UIndex));
                                                }
                                                else
                                                {
                                                    if (functionsTableIndex < 0)
                                                    {
                                                        ImportEntry functionObjIndex = importpcc.GetImport(functionsTableIndex);
                                                        IEntry newFunctionObjIndex = EntryImporter.getOrAddCrossImportOrPackage(functionObjIndex.FullPath, importpcc, exp.FileRef);
                                                        newdata.OverwriteRange(offset, BitConverter.GetBytes(newFunctionObjIndex.UIndex));
                                                    }
                                                    else
                                                    {
                                                        relinkFailedReport.Add(exp.UIndex + " " + exp.FullPath + " binary relink error: Full Functions List function[" + i + "] could not be remapped during porting: " + functionsTableIndex + " is not in the mapping tree and could not be relinked");
                                                    }
                                                }

                                                offset += 4;
                                            }
                                        }

                                        exp.Data = newdata;
                                    }
                                    catch (Exception ex)
                                    {
                                        relinkFailedReport.Add(exp.UIndex + " " + exp.FullPath + " binary relink error: Exception relinking: " + ex.Message);
                                    }
                                }
                                    break;
                                case "Function":
                                    //Crazy experimental
                                {
                                    //Oh god
                                    Bytecode.RelinkFunctionForPorting(sourceexp, exp, relinkFailedReport, crossPCCObjectMappingList);
                                }
                                    break;
                                default:
                                    continue;
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        relinkFailedReport.Add($"{exp.UIndex} {exp.FullPath} binary relinking failed due to exception: {e.Message}");
                    }
                }
            }

            crossPccObjectMap.Clear();
            crossPccObjectMap.AddRange(crossPCCObjectMappingList);
            return relinkFailedReport;
        }

        private static List<string> relinkPropertiesRecursive(IMEPackage importingPCC, ExportEntry relinkingExport, PropertyCollection transplantProps, OrderedMultiValueDictionary<IEntry, IEntry> crossPCCObjectMappingList, string debugPrefix)
        {
            var relinkResults = new List<string>();
            foreach (UProperty prop in transplantProps)
            {
                Debug.WriteLine($"{debugPrefix} Relink recursive on {prop.Name}");
                if (prop is StructProperty structProperty)
                {
                    relinkResults.AddRange(relinkPropertiesRecursive(importingPCC, relinkingExport, structProperty.Properties, crossPCCObjectMappingList, debugPrefix + "-"));
                }
                else if (prop is ArrayProperty<StructProperty> structArrayProp)
                {
                    foreach (StructProperty arrayStructProperty in structArrayProp)
                    {
                        relinkResults.AddRange(relinkPropertiesRecursive(importingPCC, relinkingExport, arrayStructProperty.Properties, crossPCCObjectMappingList, debugPrefix + "-"));
                    }
                }
                else if (prop is ArrayProperty<ObjectProperty> objArrayProp)
                {
                    foreach (ObjectProperty objProperty in objArrayProp)
                    {
                        int uIndex = objProperty.Value;
                        string result = relinkObjectProperty(importingPCC, relinkingExport, ref uIndex, objProperty.Name, crossPCCObjectMappingList, debugPrefix);
                        objProperty.Value = uIndex;
                        if (result != null)
                        {
                            relinkResults.Add(result);
                        }
                    }
                }
                else if (prop is ObjectProperty objectProperty)
                {
                    //relink
                    int uIndex = objectProperty.Value;
                    string result = relinkObjectProperty(importingPCC, relinkingExport, ref uIndex, objectProperty.Name, crossPCCObjectMappingList, debugPrefix);
                    objectProperty.Value = uIndex;
                    if (result != null)
                    {
                        relinkResults.Add(result);
                    }
                }
            }
            return relinkResults;
        }

        private static string relinkObjectProperty(IMEPackage importingPCC, ExportEntry relinkingExport, ref int uIndex, string propertyName, OrderedMultiValueDictionary<IEntry, IEntry> crossPCCObjectMappingList, string debugPrefix)
        {
            if (uIndex == 0)
            {
                return null; //do not relink 0
            }
            if (importingPCC == relinkingExport.FileRef && uIndex < 0)
            {
                return null; //do not relink same-pcc imports.
            }
            int sourceObjReference = uIndex;

            Debug.WriteLine($"{debugPrefix} Relinking:{propertyName}");

            if (crossPCCObjectMappingList.TryGetValue(entry => entry.UIndex == sourceObjReference, out IEntry targetEntry))
            {
                //relink
                uIndex = targetEntry.UIndex;

                Debug.WriteLine($"{debugPrefix} Relink hit: {sourceObjReference}{propertyName} : {targetEntry.FullPath}");
            }
            else if (uIndex < 0) //It's an unmapped import
            {
                //objProperty is currently pointing to importingPCC as that is where we read the properties from
                int n = uIndex;
                int origvalue = n;
                //Debug.WriteLine("Relink miss, attempting JIT relink on " + n + " " + rootNode.Text);
                if (importingPCC.IsImport(n))
                {
                    //Get the original import
                    ImportEntry origImport = importingPCC.GetImport(n);
                    string origImportFullName = origImport.FullPath;
                    //Debug.WriteLine("We should import " + origImport.GetFullPath);

                    IEntry crossImport = null;
                    string linkFailedDueToError = null;
                    try
                    {
                        crossImport = EntryImporter.getOrAddCrossImportOrPackage(origImportFullName, importingPCC, relinkingExport.FileRef);
                    }
                    catch (Exception e)
                    {
                        //Error during relink
                        DebugOutput.StartDebugger("PCC Relinker");
                        DebugOutput.PrintLn("Exception occured during relink: ");
                        DebugOutput.PrintLn(ExceptionHandlerDialogWPF.FlattenException(e));
                        DebugOutput.PrintLn("You may want to consider discarding this sessions' changes as relinking was not able to properly finish.");
                        linkFailedDueToError = e.Message;
                    }

                    if (crossImport != null)
                    {
                        crossPCCObjectMappingList.Add(origImport, crossImport); //add to mapping to speed up future relinks
                        uIndex = crossImport.UIndex;
                        Debug.WriteLine($"Relink hit: Dynamic CrossImport for {origvalue} {importingPCC.GetEntry(origvalue).FullPath} -> {uIndex}");

                    }
                    else
                    {
                        string path = importingPCC.GetEntry(uIndex) != null ? importingPCC.GetEntry(uIndex).FullPath : "Entry not found: " + uIndex;

                        if (linkFailedDueToError != null)
                        {
                            Debug.WriteLine($"Relink failed: CrossImport porting failed for {relinkingExport.ObjectName.Instanced} {relinkingExport.UIndex}: {propertyName} ({uIndex}): {importingPCC.GetEntry(origvalue).FullPath}");
                            return $"Relink failed for {propertyName} {uIndex} in export {path}({relinkingExport.UIndex}): {linkFailedDueToError}";
                        }

                        if (relinkingExport.FileRef.GetEntry(uIndex) != null)
                        {
                            Debug.WriteLine($"Relink failed: CrossImport porting failed for {relinkingExport.ObjectName.Instanced} {relinkingExport.UIndex}: {propertyName} ({uIndex}): {importingPCC.GetEntry(origvalue).FullPath}");
                            return $"Relink failed: CrossImport porting failed for {propertyName} {uIndex} {relinkingExport.FileRef.GetEntry(uIndex).FullPath} in export {relinkingExport.FullPath}({relinkingExport.UIndex})";
                        }

                        return $"Relink failed: New export does not exist - this is probably a bug in cross import code for {propertyName} {uIndex} in export {relinkingExport.FullPath}({relinkingExport.UIndex})";
                    }
                }
            }
            else
            {
                //It's an export
                //Attempt lookup
                ExportEntry sourceExport = importingPCC.GetUExport(uIndex);
                string fullPath = sourceExport.FullPath;
                int indexValue = sourceExport.indexValue;
                var existingExport = relinkingExport.FileRef.Exports.FirstOrDefault(x => x.FullPath == fullPath && indexValue == x.indexValue);
                if (existingExport != null)
                {
                    Debug.WriteLine($"Relink hit [EXPERIMENTAL]: Existing export in file was found, linking to it:  " +
                                    $"{uIndex} {sourceExport.InstancedFullPath} -> {existingExport.InstancedFullPath}");
                    uIndex = existingExport.UIndex;

                }
                else
                {
                    string path = importingPCC.GetEntry(uIndex)?.FullPath ?? $"Entry not found: {uIndex}";
                    Debug.WriteLine($"Relink failed in {relinkingExport.ObjectName.Instanced} {relinkingExport.UIndex}: {propertyName} {uIndex} {path}");
                    return $"Relink failed: {propertyName} {uIndex} in export {relinkingExport.FullPath}({relinkingExport.UIndex})";
                }
            }

            return null;
        }

        private static int ClassParser_RelinkComponentsTable(IMEPackage importpcc, ExportEntry exp, List<string> relinkFailedReport, ref byte[] data, int offset, Dictionary<IEntry, IEntry> crossPccObjectMap)
        {
            if (importpcc.Game == MEGame.ME3)
            {
                int componentTableNameIndex = BitConverter.ToInt32(data, offset);
                int componentTableIndex = BitConverter.ToInt32(data, offset + 4);
                NameReference importingName = importpcc.GetNameEntry(componentTableNameIndex);
                int newComponentTableName = exp.FileRef.FindNameOrAdd(importingName);
                data.OverwriteRange(offset, BitConverter.GetBytes(newComponentTableName));
                offset += 8;

                int componentTableCount = BitConverter.ToInt32(data, offset);
                offset += 4;

                for (int i = 0; i < componentTableCount; i++)
                {
                    int nameTableIndex = BitConverter.ToInt32(data, offset);
                    int nameIndex = BitConverter.ToInt32(data, offset + 4);
                    importingName = importpcc.GetNameEntry(nameTableIndex);
                    int componentName = exp.FileRef.FindNameOrAdd(importingName);
                    data.OverwriteRange(offset, BitConverter.GetBytes(componentName));
                    offset += 8;

                    int componentObjectIndex = BitConverter.ToInt32(data, offset);

                    //TODO: Add lookup
                    if (crossPccObjectMap.TryGetValue(importpcc.GetEntry(componentObjectIndex), out IEntry mapped))
                    {
                        data.OverwriteRange(offset, BitConverter.GetBytes(mapped.UIndex));
                    }
                    else
                    {
                        if (componentObjectIndex < 0)
                        {
                            ImportEntry componentObjectImport = importpcc.GetImport(componentObjectIndex);
                            IEntry newComponentObjectImport = EntryImporter.getOrAddCrossImportOrPackage(componentObjectImport.FullPath, importpcc, exp.FileRef);
                            data.OverwriteRange(offset, BitConverter.GetBytes(newComponentObjectImport.UIndex));
                        }
                        else if (componentObjectIndex > 0) //we do not remap on 0 here in binary land
                        {
                            relinkFailedReport.Add($"{exp.UIndex} {exp.FullPath} binary relink error: Component[{i}] could not be remapped during porting: {componentObjectIndex} is not in the mapping tree");
                        }
                    }
                    offset += 4;
                }
            }
            else
            {
                int componentTableCount = BitConverter.ToInt32(data, offset);
                offset += 4;

                for (int i = 0; i < componentTableCount; i++)
                {
                    int nameTableIndex = BitConverter.ToInt32(data, offset);
                    int nameIndex = BitConverter.ToInt32(data, offset + 4);

                    offset += 8;

                    int componentObjectIndex = BitConverter.ToInt32(data, offset);
                    if (componentObjectIndex != 0)
                    {
                        if (crossPccObjectMap.TryGetValue(importpcc.GetEntry(componentObjectIndex), out IEntry mapped))
                        {
                            data.OverwriteRange(offset, BitConverter.GetBytes(mapped.UIndex));
                        }
                        else
                        {
                            if (componentObjectIndex < 0)
                            {
                                ImportEntry componentObjectImport = importpcc.GetImport(componentObjectIndex);
                                IEntry newComponentObjectImport = EntryImporter.getOrAddCrossImportOrPackage(componentObjectImport.FullPath, importpcc, exp.FileRef);
                                data.OverwriteRange(offset, BitConverter.GetBytes(newComponentObjectImport.UIndex));
                            }
                            else
                            {
                                relinkFailedReport.Add("Binary Class Component[" + i + "] could not be remapped during porting: " + componentObjectIndex + " is not in the mapping tree");
                            }
                        }
                    }
                    offset += 4;
                }
            }
            return offset;
        }

        private static int ClassParser_ReadImplementsTable(IMEPackage importpcc, ExportEntry exp, List<string> relinkFailedReport, ref byte[] data, int offset, Dictionary<IEntry, IEntry> crossPccObjectMap)
        {
            if (importpcc.Game == MEGame.ME3)
            {
                int interfaceCount = BitConverter.ToInt32(data, offset);
                offset += 4;
                for (int i = 0; i < interfaceCount; i++)
                {
                    { //scoped
                        int interfaceIndex = BitConverter.ToInt32(data, offset);
                        if (crossPccObjectMap.TryGetValue(importpcc.GetEntry(interfaceIndex), out IEntry mapped))
                        {
                            data.OverwriteRange(offset, BitConverter.GetBytes(mapped.UIndex));
                        }
                        else
                        {
                            relinkFailedReport.Add("Binary Class Interface Index[" + i +
                                                   "] could not be remapped during porting: " + interfaceIndex +
                                                   " is not in the mapping tree");
                        }
                    }

                    offset += 4;

                    //propertypointer
                    {
                        int propertyPointerIndex = BitConverter.ToInt32(data, offset);

                        if (crossPccObjectMap.TryGetValue(importpcc.GetEntry(propertyPointerIndex), out IEntry mapped))
                        {
                            data.OverwriteRange(offset, BitConverter.GetBytes(mapped.UIndex));
                        }
                        else
                        {
                            relinkFailedReport.Add("Binary Class Interface Index[" + i +
                                                   "] could not be remapped during porting: " + propertyPointerIndex +
                                                   " is not in the mapping tree");
                        }
                    }
                    offset += 4;
                }
            }
            else
            {
                int interfaceTableName = BitConverter.ToInt32(data, offset); //????
                NameReference importingName = importpcc.GetNameEntry(interfaceTableName);
                int interfaceName = exp.FileRef.FindNameOrAdd(importingName);
                data.OverwriteRange(offset, BitConverter.GetBytes(interfaceName));
                offset += 8;

                int interfaceCount = BitConverter.ToInt32(data, offset);
                offset += 4;
                for (int i = 0; i < interfaceCount; i++)
                {
                    int interfaceNameIndex = BitConverter.ToInt32(data, offset);
                    importingName = importpcc.GetNameEntry(interfaceNameIndex);
                    interfaceName = exp.FileRef.FindNameOrAdd(importingName);
                    data.OverwriteRange(offset, BitConverter.GetBytes(interfaceName));
                    offset += 8;
                }
            }
            return offset;
        }
    }
}