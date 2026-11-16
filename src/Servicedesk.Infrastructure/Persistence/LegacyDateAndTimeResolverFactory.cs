using Npgsql.Internal;
using Npgsql.Internal.Postgres;
using NpgsqlTypes;

namespace Servicedesk.Infrastructure.Persistence;

// Npgsql 10 maps PostgreSQL `date` / `time` to DateOnly / TimeOnly for
// non-generic reads (GetValue — what Dapper uses). The Dapper models read
// these columns into DateTime / TimeSpan, so this resolver restores the
// pre-10 mapping. Verbatim copy of the resolver Npgsql publishes for exactly
// this purpose (https://www.npgsql.org/static/LegacyDateAndTimeResolverFactory.cs,
// linked from the Npgsql 10.0 release notes; PostgreSQL License), only made
// internal and namespaced. Registered by ServicedeskDataSource.Create — never
// build an NpgsqlDataSource without it.
#pragma warning disable NPG9001 // Npgsql.Internal is an experimental API surface.

internal sealed class LegacyDateAndTimeResolverFactory : PgTypeInfoResolverFactory
{
    public override IPgTypeInfoResolver CreateResolver() => new Resolver();
    public override IPgTypeInfoResolver CreateArrayResolver() => new ArrayResolver();
    public override IPgTypeInfoResolver CreateRangeResolver() => new RangeResolver();
    public override IPgTypeInfoResolver CreateRangeArrayResolver() => new RangeArrayResolver();
    public override IPgTypeInfoResolver CreateMultirangeResolver() => new MultirangeResolver();
    public override IPgTypeInfoResolver CreateMultirangeArrayResolver() => new MultirangeArrayResolver();

    const string Date = "pg_catalog.date";
    const string Time = "pg_catalog.time";
    const string DateRange = "pg_catalog.daterange";
    const string DateMultirange = "pg_catalog.datemultirange";

    class Resolver : IPgTypeInfoResolver
    {
        TypeInfoMappingCollection? _mappings;
        protected TypeInfoMappingCollection Mappings => _mappings ??= AddMappings(new());

        public PgTypeInfo? GetTypeInfo(Type? type, DataTypeName? dataTypeName, PgSerializerOptions options)
            => type == typeof(object) ? Mappings.Find(type, dataTypeName, options) : null;

        static TypeInfoMappingCollection AddMappings(TypeInfoMappingCollection mappings)
        {
            mappings.AddStructType<DateTime>(Date,
                static (options, mapping, _) => options.GetTypeInfo(typeof(DateTime), new DataTypeName(mapping.DataTypeName))!,
                matchRequirement: MatchRequirement.DataTypeName);

            mappings.AddStructType<TimeSpan>(Time,
                static (options, mapping, _) => options.GetTypeInfo(typeof(TimeSpan), new DataTypeName(mapping.DataTypeName))!,
                isDefault: true);

            return mappings;
        }
    }

    sealed class ArrayResolver : Resolver, IPgTypeInfoResolver
    {
        TypeInfoMappingCollection? _mappings;
        new TypeInfoMappingCollection Mappings => _mappings ??= AddMappings(new(base.Mappings));

        public new PgTypeInfo? GetTypeInfo(Type? type, DataTypeName? dataTypeName, PgSerializerOptions options)
            => type == typeof(object) ? Mappings.Find(type, dataTypeName, options) : null;

        static TypeInfoMappingCollection AddMappings(TypeInfoMappingCollection mappings)
        {
            mappings.AddStructArrayType<DateTime>(Date);
            mappings.AddStructArrayType<TimeSpan>(Time);

            return mappings;
        }
    }

    class RangeResolver : IPgTypeInfoResolver
    {
        TypeInfoMappingCollection? _mappings;
        protected TypeInfoMappingCollection Mappings => _mappings ??= AddMappings(new());

        public PgTypeInfo? GetTypeInfo(Type? type, DataTypeName? dataTypeName, PgSerializerOptions options)
            => type == typeof(object) ? Mappings.Find(type, dataTypeName, options) : null;

        static TypeInfoMappingCollection AddMappings(TypeInfoMappingCollection mappings)
        {
            mappings.AddStructType<NpgsqlRange<DateTime>>(DateRange,
                static (options, mapping, _) => options.GetTypeInfo(typeof(NpgsqlRange<DateTime>), new DataTypeName(mapping.DataTypeName))!,
                matchRequirement: MatchRequirement.DataTypeName);

            return mappings;
        }
    }

    sealed class RangeArrayResolver : RangeResolver, IPgTypeInfoResolver
    {
        TypeInfoMappingCollection? _mappings;
        new TypeInfoMappingCollection Mappings => _mappings ??= AddMappings(new(base.Mappings));

        public new PgTypeInfo? GetTypeInfo(Type? type, DataTypeName? dataTypeName, PgSerializerOptions options)
            => type == typeof(object) ? Mappings.Find(type, dataTypeName, options) : null;

        static TypeInfoMappingCollection AddMappings(TypeInfoMappingCollection mappings)
        {
            mappings.AddStructArrayType<NpgsqlRange<DateTime>>(DateRange);

            return mappings;
        }
    }

    class MultirangeResolver : IPgTypeInfoResolver
    {
        TypeInfoMappingCollection? _mappings;
        protected TypeInfoMappingCollection Mappings => _mappings ??= AddMappings(new());

        public PgTypeInfo? GetTypeInfo(Type? type, DataTypeName? dataTypeName, PgSerializerOptions options)
            => type == typeof(object) ? Mappings.Find(type, dataTypeName, options) : null;

        static TypeInfoMappingCollection AddMappings(TypeInfoMappingCollection mappings)
        {
            mappings.AddType<NpgsqlRange<DateTime>[]>(DateMultirange,
                static (options, mapping, _) => options.GetTypeInfo(typeof(NpgsqlRange<DateTime>[]), new DataTypeName(mapping.DataTypeName))!,
                matchRequirement: MatchRequirement.DataTypeName);

            return mappings;
        }
    }

    sealed class MultirangeArrayResolver : MultirangeResolver, IPgTypeInfoResolver
    {
        TypeInfoMappingCollection? _mappings;
        new TypeInfoMappingCollection Mappings => _mappings ??= AddMappings(new(base.Mappings));

        public new PgTypeInfo? GetTypeInfo(Type? type, DataTypeName? dataTypeName, PgSerializerOptions options)
            => type == typeof(object) ? Mappings.Find(type, dataTypeName, options) : null;

        static TypeInfoMappingCollection AddMappings(TypeInfoMappingCollection mappings)
        {
            mappings.AddArrayType<NpgsqlRange<DateTime>[]>(DateMultirange);

            return mappings;
        }
    }
}
#pragma warning restore NPG9001
