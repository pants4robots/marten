using System;
using System.Collections.Generic;
using System.Linq;
using Baseline;
using Marten.Schema;
using Marten.Util;
using Npgsql;
using Remotion.Linq;
using Remotion.Linq.Clauses;
using Remotion.Linq.Clauses.Expressions;
using Remotion.Linq.Clauses.ResultOperators;

namespace Marten.Linq
{
    public class DocumentQuery
    {
        private readonly IDocumentMapping _mapping;
        private readonly QueryModel _query;
        private readonly MartenExpressionParser _parser;

        public DocumentQuery(IDocumentMapping mapping, QueryModel query, MartenExpressionParser parser)
        {
            _mapping = mapping;
            _query = query;
            _parser = parser;
        }

        public void ConfigureForAny(NpgsqlCommand command)
        {
            var sql = "select (count(*) > 0) as result from " + _mapping.TableName + " as d";

            var where = buildWhereClause();

            if (@where != null) sql += " where " + @where.ToSql(command);

            command.AppendQuery(sql);
        }

        public void ConfigureForCount(NpgsqlCommand command)
        {
            var sql = "select count(*) as number from " + _mapping.TableName + " as d";

            var where = buildWhereClause();

            if (@where != null) sql += " where " + @where.ToSql(command);

            command.AppendQuery(sql);

        }

        public void ConfigureForSum(NpgsqlCommand command)
        {
            var propToSum = _mapping.JsonLocator(_query.SelectClause.Selector);
            var sql = $"select sum({propToSum}) as number from {_mapping.TableName} as d";

            var where = buildWhereClause();

            if (@where != null) sql += " where " + @where.ToSql(command);

            command.AppendQuery(sql);
        }


        public ISelector<T> ConfigureCommand<T>(IDocumentSchema schema, NpgsqlCommand command)
        {
            if (_query.ResultOperators.OfType<LastResultOperator>().Any())
            {
                throw new InvalidOperationException("Marten does not support the Last() or LastOrDefault() operations. Use a combination of ordering and First/FirstOrDefault() instead");
            }

            var documentStorage = schema.StorageFor(typeof(T));
            return ConfigureCommand<T>(documentStorage, command);
        }

        public ISelector<T> ConfigureCommand<T>(IDocumentStorage documentStorage, NpgsqlCommand command)
        {
            var select = buildSelectClause<T>(documentStorage);
            var sql = $"select {@select.SelectClause(_mapping)} from {_mapping.TableName} as d";

            var where = buildWhereClause();
            var orderBy = toOrderClause();

            if (@where != null) sql += " where " + @where.ToSql(command);

            if (orderBy.IsNotEmpty()) sql += orderBy;

            sql = appendLimit(sql);
            sql = appendOffset(sql);

            command.AppendQuery(sql);

            return @select;
        }

        private ISelector<T> buildSelectClause<T>(IDocumentStorage storage)
        {
            if (_query.SelectClause.Selector.Type == _query.MainFromClause.ItemType)
            {
                return new WholeDocumentSelector<T>(storage.As<IResolver<T>>());
            }
            
            throw new NotImplementedException("Cannot yet do a Select() projection");
        }

        private string appendOffset(string sql)
        {
            var take =
                _query.ResultOperators.OfType<SkipResultOperator>().OrderByDescending(x => x.Count).FirstOrDefault();

            return take == null ? sql : sql + " OFFSET " + take.Count + " ";
        }

        private string appendLimit(string sql)
        {
            var take =
                _query.ResultOperators.OfType<TakeResultOperator>().OrderByDescending(x => x.Count).FirstOrDefault();

            string limitNumber = null;
            if (take != null)
            {
                limitNumber = take.Count.ToString();
            }
            else if (_query.ResultOperators.Any(x => x is FirstResultOperator))
            {
                limitNumber = "1";
            }
            // Got to return more than 1 to make it fail if there is more than one in the db
            else if (_query.ResultOperators.Any(x => x is SingleResultOperator))
            {
                limitNumber = "2";
            }

            return limitNumber == null ? sql : sql + " LIMIT " + limitNumber + " ";
        }

        private string toOrderClause()
        {
            var orders = _query.BodyClauses.OfType<OrderByClause>().SelectMany(x => x.Orderings).ToArray();
            if (!orders.Any()) return string.Empty;

            return " order by " + orders.Select(ToOrderClause).Join(", ");
        }

        public string ToOrderClause(Ordering clause)
        {
            var locator = _mapping.JsonLocator(clause.Expression);
            return clause.OrderingDirection == OrderingDirection.Asc
                ? locator
                : locator + " desc";
        }

        private IWhereFragment buildWhereClause()
        {
            var wheres = _query.BodyClauses.OfType<WhereClause>().ToArray();
            if (wheres.Length == 0) return _mapping.DefaultWhereFragment();

            var where = wheres.Length == 1
                ? _parser.ParseWhereFragment(_mapping, wheres.Single().Predicate)
                : new CompoundWhereFragment(_parser, _mapping, "and", wheres);

            return _mapping.FilterDocuments(where);
        }
    }

    
}