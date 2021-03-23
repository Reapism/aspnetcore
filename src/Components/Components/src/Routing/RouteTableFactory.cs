// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.AspNetCore.Components.Routing;

namespace Microsoft.AspNetCore.Components
{
    /// <summary>
    /// Resolves components for an application.
    /// </summary>
    internal static class RouteTableFactory
    {
        private static readonly ConcurrentDictionary<Key, RouteTable> Cache = new();
        public static readonly IComparer<RouteEntry> RoutePrecedence = Comparer<RouteEntry>.Create(RouteComparison);

        public static RouteTable Create(IReadOnlySet<Assembly> assemblies)
        {
            var key = new Key(assemblies);
            if (Cache.TryGetValue(key, out var resolvedComponents))
            {
                return resolvedComponents;
            }

            var componentTypes = GetRouteableComponents(assemblies);
            var routeTable = Create(componentTypes);
            Cache.TryAdd(key, routeTable);
            return routeTable;
        }

        internal static List<Type> GetRouteableComponents(IEnumerable<Assembly> assemblies)
        {
            var routeableComponents = new List<Type>();
            foreach (var assembly in assemblies)
            {
                GetRouteableComponents(routeableComponents, assembly);
            }

            return routeableComponents;
        }

        private static void GetRouteableComponents(List<Type> routeableComponents, Assembly assembly)
        {
            foreach (var type in assembly.ExportedTypes)
            {
                if (typeof(IComponent).IsAssignableFrom(type) && type.IsDefined(typeof(RouteAttribute)))
                {
                    routeableComponents.Add(type);
                }
            }
        }

        internal static RouteTable Create(List<Type> componentTypes)
        {
            var templatesByHandler = new Dictionary<Type, List<string>>();
            foreach (var componentType in componentTypes)
            {
                // We're deliberately using inherit = false here.
                //
                // RouteAttribute is defined as non-inherited, because inheriting a route attribute always causes an
                // ambiguity. You end up with two components (base class and derived class) with the same route.
                var routeAttributes = componentType.GetCustomAttributes<RouteAttribute>(inherit: false);
                var templates = new List<string>();
                foreach (var attribute in routeAttributes)
                {
                    templates.Add(attribute.Template);
                }

                templatesByHandler.Add(componentType, templates);
            }
            return Create(templatesByHandler);
        }

        internal static RouteTable Create(Dictionary<Type, List<string>> templatesByHandler)
        {
            var routes = new List<RouteEntry>();
            foreach (var (type, templates) in templatesByHandler)
            {
                var allRouteParameterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var parsedTemplates = new (RouteTemplate, List<string>)[templates.Count];
                for (var i = 0; i < templates.Count; i++)
                {
                    var parsedTemplate = TemplateParser.ParseTemplate(templates[i]);
                    var parameterNames = GetParameterNames(parsedTemplate);
                    parsedTemplates[i] = (parsedTemplate, parameterNames);

                    foreach (var parameterName in parameterNames)
                    {
                        allRouteParameterNames.Add(parameterName);
                    }
                }

                foreach (var (parsedTemplate, routeParameterNames) in parsedTemplates)
                {
                    var unusedRouteParameterNames = GetUnusedParameterNames(allRouteParameterNames, routeParameterNames);
                    var entry = new RouteEntry(parsedTemplate, type, unusedRouteParameterNames);
                    routes.Add(entry);
                }
            }

            routes.Sort(RoutePrecedence);
            return new RouteTable(routes.ToArray());
        }

        private static List<string> GetParameterNames(RouteTemplate routeTemplate)
        {
            var parameterNames = new List<string>();
            foreach (var segment in routeTemplate.Segments)
            {
                if (!segment.IsParameter)
                {
                    continue;
                }

                parameterNames.Add(segment.Value);
            }

            return parameterNames;
        }

        private static IReadOnlyList<string> GetUnusedParameterNames(HashSet<string> allRouteParameterNames, List<string> routeParameterNames)
        {
            List<string>? unusedParameters = null;
            foreach (var item in routeParameterNames)
            {
                if (!allRouteParameterNames.Contains(item))
                {
                    unusedParameters ??= new();
                    unusedParameters.Add(item);
                }
            }

            return unusedParameters ?? (IReadOnlyList<string>)Array.Empty<string>();
        }

        /// <summary>
        /// Route precedence algorithm.
        /// We collect all the routes and sort them from most specific to
        /// less specific. The specificity of a route is given by the specificity
        /// of its segments and the position of those segments in the route.
        /// * A literal segment is more specific than a parameter segment.
        /// * A parameter segment with more constraints is more specific than one with fewer constraints
        /// * Segment earlier in the route are evaluated before segments later in the route.
        /// For example:
        /// /Literal is more specific than /Parameter
        /// /Route/With/{parameter} is more specific than /{multiple}/With/{parameters}
        /// /Product/{id:int} is more specific than /Product/{id}
        ///
        /// Routes can be ambiguous if:
        /// They are composed of literals and those literals have the same values (case insensitive)
        /// They are composed of a mix of literals and parameters, in the same relative order and the
        /// literals have the same values.
        /// For example:
        /// * /literal and /Literal
        /// /{parameter}/literal and /{something}/literal
        /// /{parameter:constraint}/literal and /{something:constraint}/literal
        ///
        /// To calculate the precedence we sort the list of routes as follows:
        /// * Shorter routes go first.
        /// * A literal wins over a parameter in precedence.
        /// * For literals with different values (case insensitive) we choose the lexical order
        /// * For parameters with different numbers of constraints, the one with more wins
        /// If we get to the end of the comparison routing we've detected an ambiguous pair of routes.
        /// </summary>
        internal static int RouteComparison(RouteEntry x, RouteEntry y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            var xTemplate = x.Template;
            var yTemplate = y.Template;
            var minSegments = Math.Min(xTemplate.Segments.Length, yTemplate.Segments.Length);
            var currentResult = 0;
            for (var i = 0; i < minSegments; i++)
            {
                var xSegment = xTemplate.Segments[i];
                var ySegment = yTemplate.Segments[i];

                var xRank = GetRank(xSegment);
                var yRank = GetRank(ySegment);

                currentResult = xRank.CompareTo(yRank);

                // If they are both literals we can disambiguate
                if ((xRank, yRank) == (0, 0))
                {
                    currentResult = StringComparer.OrdinalIgnoreCase.Compare(xSegment.Value, ySegment.Value);
                }

                if (currentResult != 0)
                {
                    break;
                }
            }

            if (currentResult == 0)
            {
                currentResult = xTemplate.Segments.Length.CompareTo(yTemplate.Segments.Length);
            }

            if (currentResult == 0)
            {
                throw new InvalidOperationException($@"The following routes are ambiguous:
'{x.Template.TemplateText}' in '{x.Handler.FullName}'
'{y.Template.TemplateText}' in '{y.Handler.FullName}'
");
            }

            return currentResult;
        }

        private static int GetRank(TemplateSegment xSegment)
        {
            return xSegment switch
            {
                // Literal
                { IsParameter: false } => 0,
                // Parameter with constraints
                { IsParameter: true, IsCatchAll: false, Constraints: { Length: > 0 } } => 1,
                // Parameter without constraints
                { IsParameter: true, IsCatchAll: false, Constraints: { Length: 0 } } => 2,
                // Catch all parameter with constraints
                { IsParameter: true, IsCatchAll: true, Constraints: { Length: > 0 } } => 3,
                // Catch all parameter without constraints
                { IsParameter: true, IsCatchAll: true, Constraints: { Length: 0 } } => 4,
                // The segment is not correct
                _ => throw new InvalidOperationException($"Unknown segment definition '{xSegment}.")
            };
        }

        private readonly struct Key : IEquatable<Key>
        {
            public readonly IReadOnlySet<Assembly>? Assemblies;
            public readonly Assembly? Assembly;

            public Key(Assembly assembly)
            {
                Assembly = assembly;
                Assemblies = null;
            }

            public Key(IReadOnlySet<Assembly> assemblies)
            {
                Assembly = null;
                Assemblies = assemblies;
            }

            public override bool Equals(object? obj)
            {
                return obj is Key other && Equals(other);
            }

            public bool Equals(Key other)
            {
                if (Assembly != null && other.Assembly != null)
                {
                    return Assembly.Equals(other.Assembly);
                }
                else if (Assemblies != null && other.Assemblies != null)
                {
                    return Assemblies.SetEquals(other.Assemblies);
                }

                return false;
            }

            public override int GetHashCode()
            {
                if (Assembly != null)
                {
                    return Assembly.GetHashCode();
                }

                var hash = new HashCode();

                if (Assemblies != null)
                {
                    foreach (var assembly in Assemblies)
                    {
                        hash.Add(assembly);
                    }
                }

                return hash.ToHashCode();
            }
        }
    }
}
