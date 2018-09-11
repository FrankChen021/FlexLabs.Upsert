﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using FlexLabs.EntityFrameworkCore.Upsert.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace FlexLabs.EntityFrameworkCore.Upsert.Runners
{
    public abstract class RelationalUpsertCommandRunner : UpsertCommandRunnerBase
    {
        protected abstract string GenerateCommand(IEntityType entityType, int entityCount, ICollection<string> insertColumns,
            ICollection<string> joinColumns, List<(string ColumnName, KnownExpression Value)> updateExpressions);
        protected abstract string Column(string name);
        protected virtual string Parameter(int index) => "@p" + index;
        protected abstract string SourcePrefix { get; }
        protected virtual string SourceSuffix => null;
        protected abstract string TargetPrefix { get; }

        private (string SqlCommand, IEnumerable<object> Arguments) PrepareCommand<TEntity>(IEntityType entityType, ICollection<TEntity> entities,
            Expression<Func<TEntity, object>> match, Expression<Func<TEntity, TEntity>> updater)
        {
            var joinColumns = ProcessMatchExpression(entityType, match);
            var joinColumnNames = joinColumns.Select(c => c.PropertyMetadata.Relational().ColumnName).ToArray();

            var properties = entityType.GetProperties()
                .Where(p => p.ValueGenerated == ValueGenerated.Never)
                .Select(p => (MetaProperty: p, PropertyInfo: typeof(TEntity).GetProperty(p.Name)))
                .Where(x => x.PropertyInfo != null)
                .ToList();
            var allColumns = properties.Select(x => x.MetaProperty.Relational().ColumnName).ToList();

            var updateExpressions = new List<(IProperty Property, KnownExpression Value)>();;
            if (updater != null)
            {
                if (!(updater.Body is MemberInitExpression entityUpdater))
                    throw new ArgumentException("updater must be an Initialiser of the TEntity type", nameof(updater));

                foreach (MemberAssignment binding in entityUpdater.Bindings)
                {
                    var property = entityType.FindProperty(binding.Member.Name);
                    if (property == null)
                        throw new InvalidOperationException("Unknown property " + binding.Member.Name);

                    var value = binding.Expression.GetValue<TEntity>(updater);
                    if (!(value is KnownExpression knownExp))
                        knownExp = new KnownExpression(ExpressionType.Constant, new ConstantValue(value));

                    if (knownExp.Value1 is ExpressionParameterProperty epp1)
                        epp1.Property = entityType.FindProperty(epp1.PropertyName);
                    if (knownExp.Value2 is ExpressionParameterProperty epp2)
                        epp2.Property = entityType.FindProperty(epp2.PropertyName);
                    updateExpressions.Add((property, knownExp));
                }
            }
            else
            {
                foreach (var property in properties)
                {
                    if (joinColumnNames.Contains(property.MetaProperty.Relational().ColumnName))
                        continue;

                    var propertyAccess = new ExpressionParameterProperty(property.MetaProperty.Name, false) { Property = property.MetaProperty };
                    var updateExpression = new KnownExpression(ExpressionType.MemberAccess, propertyAccess);
                    updateExpressions.Add((property.MetaProperty, updateExpression));
                }
            }

            var arguments = entities.SelectMany(e => properties.Select(p => new ConstantValue(p.PropertyInfo.GetValue(e)))).ToList();
            arguments.AddRange(updateExpressions.SelectMany(e => new[] { e.Value.Value1, e.Value.Value2 }).OfType<ConstantValue>());
            int i = 0;
            foreach (var arg in arguments)
                arg.ParameterIndex = i++;

            var columnUpdateExpressions = updateExpressions.Select(x => (x.Property.Relational().ColumnName, x.Value)).ToList();
            var sqlCommand = GenerateCommand(entityType, entities.Count, allColumns, joinColumnNames, columnUpdateExpressions);
            return (sqlCommand, arguments.Select(a => a.Value));
        }

        private string ExpandValue(IKnownValue value)
        {
            switch (value)
            {
                case ExpressionParameterProperty prop:
                    var prefix = prop.IsLeftParameter ? TargetPrefix : SourcePrefix;
                    return prefix + Column(prop.Property.Relational().ColumnName);

                case ConstantValue constVal:
                    return Parameter(constVal.ParameterIndex);

                default:
                    throw new InvalidOperationException();
            }
        }

        protected virtual string ExpandExpression(KnownExpression expression)
        {
            switch (expression.ExpressionType)
            {
                case ExpressionType.Add:
                case ExpressionType.Divide:
                case ExpressionType.Multiply:
                case ExpressionType.Subtract:
                    var left = ExpandValue(expression.Value1);
                    var right = ExpandValue(expression.Value2);
                    var op = GetSimpleOperator(expression.ExpressionType);
                    return $"{left} {op} {right}";

                case ExpressionType.MemberAccess:
                case ExpressionType.Constant:
                    return ExpandValue(expression.Value1);

                default: throw new NotSupportedException("Don't know how to process operation: " + expression.ExpressionType);
            }
        }

        protected virtual string GetSimpleOperator(ExpressionType expressionType)
        {
            switch (expressionType)
            {
                case ExpressionType.Add: return "+";
                case ExpressionType.Divide: return "/";
                case ExpressionType.Multiply: return "*";
                case ExpressionType.Subtract: return "-";
                default: throw new InvalidOperationException($"{expressionType} is not a simple arithmetic operation");
            }
        }

        public override void Run<TEntity>(DbContext dbContext, IEntityType entityType, ICollection<TEntity> entities, Expression<Func<TEntity, object>> matchExpression,
            Expression<Func<TEntity, TEntity>> updateExpression)
        {
            var (sqlCommand, arguments) = PrepareCommand(entityType, entities, matchExpression, updateExpression);
            dbContext.Database.ExecuteSqlCommand(sqlCommand, arguments);
        }

        public override Task RunAsync<TEntity>(DbContext dbContext, IEntityType entityType, ICollection<TEntity> entities, Expression<Func<TEntity, object>> matchExpression,
            Expression<Func<TEntity, TEntity>> updateExpression, CancellationToken cancellationToken)
        {
            var (sqlCommand, arguments) = PrepareCommand(entityType, entities, matchExpression, updateExpression);
            return dbContext.Database.ExecuteSqlCommandAsync(sqlCommand, arguments);
        }
    }
}
