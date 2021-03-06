﻿//-----------------------------------------------------------------------
// <copyright file="TypeLoader.cs" company="Microsoft">
//     Copyright (c) Microsoft Corporation.  All rights reserved.
// </copyright>
// <summary>Determines if a type is in a given assembly and loads that type. 
// This version is CLR 3.5-compatible.</summary>
//-----------------------------------------------------------------------

using System;
using System.IO;
using System.Reflection;
using System.Collections;
using System.Globalization;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Microsoft.Build.Shared
{
    /// <summary>
    /// This class is used to load types from their assemblies.
    /// </summary>
    internal class TypeLoader
    {
        /// <summary>
        /// Lock for initializing the dictionary
        /// </summary>
        private static readonly Object cacheOfLoadedTypesByFilterLock = new Object();

        /// <summary>
        /// Lock for initializing the dictionary
        /// </summary>
        private static readonly Object cacheOfReflectionOnlyLoadedTypesByFilterLock = new Object();

        /// <summary>
        /// Lock for initializing the dictionary
        /// </summary>
        private static readonly Object loadInfoToTypeLock = new Object();

        /// <summary>
        /// Lock for initializing the dictionary
        /// </summary>
        private static readonly Object reflectionOnlyloadInfoToTypeLock = new Object();

        /// <summary>
        /// Cache to keep track of the assemblyLoadInfos based on a given typeFilter.
        /// </summary>
        private static IDictionary<TypeFilter, IDictionary<AssemblyLoadInfo, AssemblyInfoToLoadedTypes>> cacheOfLoadedTypesByFilter = new Dictionary<TypeFilter, IDictionary<AssemblyLoadInfo, AssemblyInfoToLoadedTypes>>();
        
        /// <summary>
        /// Cache to keep track of the assemblyLoadInfos based on a given typeFilter for assemblies which are to be loaded for reflectionOnlyLoads.
        /// </summary>
        private static IDictionary<TypeFilter, IDictionary<AssemblyLoadInfo, AssemblyInfoToLoadedTypes>> cacheOfReflectionOnlyLoadedTypesByFilter = new Dictionary<TypeFilter, IDictionary<AssemblyLoadInfo, AssemblyInfoToLoadedTypes>>();
       
        /// <summary>
        /// Typefilter for this typeloader
        /// </summary>
        private TypeFilter isDesiredType;

        /// <summary>
        /// Constructor.
        /// </summary>
        internal TypeLoader(TypeFilter isDesiredType)
        {
            ErrorUtilities.VerifyThrow(isDesiredType != null, "need a type filter");

            this.isDesiredType = isDesiredType;
        }

        /// <summary>
        /// Given two type names, looks for a partial match between them. A partial match is considered valid only if it occurs on
        /// the right side (tail end) of the name strings, and at the start of a class or namespace name.
        /// </summary>
        /// <remarks>
        /// 1) Matches are case-insensitive.
        /// 2) .NET conventions regarding namespaces and nested classes are respected, including escaping of reserved characters.
        /// </remarks>
        /// <example>
        /// "Csc" and "csc"                                                 ==> exact match
        /// "Microsoft.Build.Tasks.Csc" and "Microsoft.Build.Tasks.Csc"     ==> exact match
        /// "Microsoft.Build.Tasks.Csc" and "Csc"                           ==> partial match
        /// "Microsoft.Build.Tasks.Csc" and "Tasks.Csc"                     ==> partial match
        /// "MyTasks.ATask+NestedTask" and "NestedTask"                     ==> partial match
        /// "MyTasks.ATask\\+NestedTask" and "NestedTask"                   ==> partial match
        /// "MyTasks.CscTask" and "Csc"                                     ==> no match
        /// "MyTasks.MyCsc" and "Csc"                                       ==> no match
        /// "MyTasks.ATask\.Csc" and "Csc"                                  ==> no match
        /// "MyTasks.ATask\\\.Csc" and "Csc"                                ==> no match
        /// </example>
        /// <returns>true, if the type names match exactly or partially; false, if there is no match at all</returns>
        internal static bool IsPartialTypeNameMatch(string typeName1, string typeName2)
        {
            bool isPartialMatch = false;

            // if the type names are the same length, a partial match is impossible
            if (typeName1.Length != typeName2.Length)
            {
                string longerTypeName;
                string shorterTypeName;

                // figure out which type name is longer
                if (typeName1.Length > typeName2.Length)
                {
                    longerTypeName = typeName1;
                    shorterTypeName = typeName2;
                }
                else
                {
                    longerTypeName = typeName2;
                    shorterTypeName = typeName1;
                }

                // if the shorter type name matches the end of the longer one
                if (longerTypeName.EndsWith(shorterTypeName, StringComparison.OrdinalIgnoreCase))
                {
                    int matchIndex = longerTypeName.Length - shorterTypeName.Length;

                    // if the matched sub-string looks like the start of a namespace or class name
                    if ((longerTypeName[matchIndex - 1] == '.') || (longerTypeName[matchIndex - 1] == '+'))
                    {
                        int precedingBackslashes = 0;

                        // confirm there are zero, or an even number of \'s preceding it...
                        for (int i = matchIndex - 2; i >= 0; i--)
                        {
                            if (longerTypeName[i] == '\\')
                            {
                                precedingBackslashes++;
                            }
                            else
                            {
                                break;
                            }
                        }

                        if ((precedingBackslashes % 2) == 0)
                        {
                            isPartialMatch = true;
                        }
                    }
                }
            }
            else
            {
                isPartialMatch = (String.Compare(typeName1, typeName2, StringComparison.OrdinalIgnoreCase) == 0);
            }

            return isPartialMatch;
        }

        /// <summary>
        /// Loads the specified type if it exists in the given assembly. If the type name is fully qualified, then a match (if
        /// any) is unambiguous; otherwise, if there are multiple types with the same name in different namespaces, the first type
        /// found will be returned.
        /// </summary>
        internal LoadedType Load
        (
            string typeName,
            AssemblyLoadInfo assembly
        )
        {
            return GetLoadedType(cacheOfLoadedTypesByFilterLock, loadInfoToTypeLock, cacheOfLoadedTypesByFilter, typeName, assembly);
        }

        /// <summary>
        /// Loads the specified type if it exists in the given assembly. If the type name is fully qualified, then a match (if
        /// any) is unambiguous; otherwise, if there are multiple types with the same name in different namespaces, the first type
        /// found will be returned.
        /// </summary>
        /// <returns>The loaded type, or null if the type was not found.</returns>
        internal LoadedType ReflectionOnlyLoad
        (
            string typeName,
            AssemblyLoadInfo assembly
        )
        {
            return GetLoadedType(cacheOfReflectionOnlyLoadedTypesByFilterLock, reflectionOnlyloadInfoToTypeLock, cacheOfReflectionOnlyLoadedTypesByFilter, typeName, assembly);
        }

        /// <summary>
        /// Loads the specified type if it exists in the given assembly. If the type name is fully qualified, then a match (if
        /// any) is unambiguous; otherwise, if there are multiple types with the same name in different namespaces, the first type
        /// found will be returned.
        /// </summary>
        private LoadedType GetLoadedType(object cacheLock, object loadInfoToTypeLock, IDictionary<TypeFilter, IDictionary<AssemblyLoadInfo, AssemblyInfoToLoadedTypes>> cache, string typeName, AssemblyLoadInfo assembly)
        {
            // A given typefilter have been used on a number of assemblies, Based on the typefilter we will get another dictionary which 
            // will map a specific AssemblyLoadInfo to a AssemblyInfoToLoadedTypes class which knows how to find a typeName in a given assembly.
            IDictionary<AssemblyLoadInfo, AssemblyInfoToLoadedTypes> loadInfoToType = null;
            lock (cacheLock)
            {
                if (!cache.TryGetValue(isDesiredType, out loadInfoToType))
                {
                    loadInfoToType = new Dictionary<AssemblyLoadInfo, AssemblyInfoToLoadedTypes>();
                    cache.Add(isDesiredType, loadInfoToType);
                }
            }

            // Get an object which is able to take a typename and determine if it is in the assembly pointed to by the AssemblyInfo.
            AssemblyInfoToLoadedTypes typeNameToType = null;
            lock (loadInfoToTypeLock)
            {
                if (!loadInfoToType.TryGetValue(assembly, out typeNameToType))
                {
                    typeNameToType = new AssemblyInfoToLoadedTypes(isDesiredType, assembly);
                    loadInfoToType.Add(assembly, typeNameToType);
                }
            }

            return typeNameToType.GetLoadedTypeByTypeName(typeName);
        }

        /// <summary>
        /// Given a type filter and an asssemblyInfo object keep track of what types in a given assembly which match the typefilter.
        /// Also, use this information to determine if a given TypeName is in the assembly which is pointed to by the AssemblyLoadInfo object.
        /// 
        /// This type represents a combination of a type filter and an assemblyInfo object.
        /// </summary>
        private class AssemblyInfoToLoadedTypes
        {
            /// <summary>
            /// Lock to prevent two threads from using this object at the same time.
            /// Since we fill up internal structures with what is in the assembly 
            /// </summary>
            private readonly Object lockObject = new Object();

            /// <summary>
            /// Type filter to pick the correct types out of an assembly
            /// </summary>
            private TypeFilter isDesiredType;

            /// <summary>
            /// Assembly load information so we can load an assembly
            /// </summary>
            private AssemblyLoadInfo assemblyLoadInfo;

            /// <summary>
            /// What is the type for the given type name, this may be null if the typeName does not map to a type.
            /// </summary>
            private Dictionary<string, Type> typeNameToType;

            /// <summary>
            /// List of public types in the assembly which match the typefilter and their corresponding types
            /// </summary>
            private Dictionary<string, Type> publicTypeNameToType;

            /// <summary>
            /// Have we scanned the public types for this assembly yet.
            /// </summary>
            private bool haveScannedPublicTypes;

            /// <summary>
            /// If we loaded an assembly for this type.
            /// We use this information to set the LoadedType.LoadedAssembly so that this object can be used
            /// to help created AppDomains to resolve those that it could not load successfully
            /// </summary>
            private Assembly loadedAssembly;
            
            /// <summary>
            /// Given a type filter, and an assembly to load the type information from determine if a given type name is in the assembly or not.
            /// </summary>
            internal AssemblyInfoToLoadedTypes(TypeFilter typeFilter, AssemblyLoadInfo loadInfo)
            {
                ErrorUtilities.VerifyThrowArgumentNull(typeFilter, "typefilter");
                ErrorUtilities.VerifyThrowArgumentNull(loadInfo, "loadInfo");

                this.isDesiredType = typeFilter;
                this.assemblyLoadInfo = loadInfo;
                this.typeNameToType = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
                this.publicTypeNameToType = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
            }

            /// <summary>
            /// Determine if a given type name is in the assembly or not. Return null if the type is not in the assembly
            /// </summary>
            internal LoadedType GetLoadedTypeByTypeName(string typeName)
            {
                ErrorUtilities.VerifyThrowArgumentNull(typeName, "typeName");

                // Only one thread should be doing operations on this instance of the object at a time.
                lock (lockObject)
                {
                    Type type = null;

                    // Maybe we've already cracked open this assembly before. Check to see if the typeName is in the list we dont look for partial matches here
                    // this is an optimization.
                    bool foundType = typeNameToType.TryGetValue(typeName, out type);
                    if (!foundType)
                    {
                        // We could still not find the type, lets try and resolve it by doing a get type.
                        if ((assemblyLoadInfo.AssemblyName != null) && (typeName.Length > 0))
                        {
                            try
                            {
                                // try to load the type using its assembly qualified name
                                type = Type.GetType(typeName + "," + assemblyLoadInfo.AssemblyName, false /* don't throw on error */, true /* case-insensitive */);
                            }
                            catch (ArgumentException)
                            {
                                // Type.GetType() will throw this exception if the type name is invalid -- but we have no idea if it's the
                                // type or the assembly name that's the problem -- so just ignore the exception, because we're going to
                                // check the existence/validity of the assembly and type respectively, below anyway
                            }

                            // if we found the type, it means its assembly qualified name was also its fully qualified name
                            if (type != null)
                            {
                                // if it's not the right type, bail out -- there's no point searching further since we already matched on the
                                // fully qualified name
                                if (!isDesiredType(type, null))
                                {
                                    typeNameToType.Add(typeName, null);
                                    return null;
                                }
                                else
                                {
                                    typeNameToType.Add(typeName, type);
                                }
                            }
                        }

                        // We could not find the type based on the passed in type name, we now need to see if there is a type which 
                        // will match based on partially matching the typename. To do this partial matching we need to get the public types in the assembly
                        if (type == null && !haveScannedPublicTypes)
                        {
                            ScanAssemblyForPublicTypes();
                            haveScannedPublicTypes = true;
                        }

                        // Could not find the type we need to look through the types in the assembly or in our cache.
                        if (type == null)
                        {
                            foreach (KeyValuePair<string, Type> desiredTypeInAssembly in publicTypeNameToType)
                            {
                                // if type matches partially on its name
                                if (typeName.Length == 0 || TypeLoader.IsPartialTypeNameMatch(desiredTypeInAssembly.Key, typeName))
                                {
                                    type = desiredTypeInAssembly.Value;
                                    typeNameToType.Add(typeName, type);
                                    break;
                                }
                            }
                        }
                    }

                    if (type != null)
                    {
                        return new LoadedType(type, assemblyLoadInfo, loadedAssembly);
                    }

                    return null;
                }
            }
 
            /// <summary>
            /// Scan the assembly pointed to by the assemblyLoadInfo for public types. We will use these public types to do partial name matching on 
            /// to find tasks, loggers, and task factories.
            /// </summary>
            private void ScanAssemblyForPublicTypes()
            {
                // we need to search the assembly for the type...
                try
                {
                    if (assemblyLoadInfo.AssemblyName != null)
                    {
                        loadedAssembly = Assembly.Load(assemblyLoadInfo.AssemblyName);
                    }
                    else
                    {
                        loadedAssembly = Assembly.LoadFrom(assemblyLoadInfo.AssemblyFile);
                    }
                }
                catch (ArgumentException e)
                {
                    // Assembly.Load() and Assembly.LoadFrom() will throw an ArgumentException if the assembly name is invalid
                    // convert to a FileNotFoundException because it's more meaningful
                    // NOTE: don't use ErrorUtilities.VerifyThrowFileExists() here because that will hit the disk again
                    throw new FileNotFoundException(null, assemblyLoadInfo.AssemblyLocation, e);
                }

                // only look at public types
                Type[] allPublicTypesInAssembly = loadedAssembly.GetExportedTypes();
                foreach (Type publicType in allPublicTypesInAssembly)
                {
                    if (isDesiredType(publicType, null))
                    {
                        publicTypeNameToType.Add(publicType.FullName, publicType);
                    }
                }
            }
        }
    }
}
