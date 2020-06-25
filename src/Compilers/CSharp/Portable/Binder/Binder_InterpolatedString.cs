﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.
#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp
{
    internal partial class Binder
    {
        private BoundExpression BindInterpolatedString(InterpolatedStringExpressionSyntax node, DiagnosticBag diagnostics)
        {
            var builder = ArrayBuilder<BoundExpression>.GetInstance();
            var stringType = GetSpecialType(SpecialType.System_String, diagnostics, node);
            var objectType = GetSpecialType(SpecialType.System_Object, diagnostics, node);
            var intType = GetSpecialType(SpecialType.System_Int32, diagnostics, node);
            ConstantValue? resultConstant = null;
            bool constantEnabled = true;
            foreach (var content in node.Contents)
            {
                switch (content.Kind())
                {
                    case SyntaxKind.Interpolation:
                        {
                            var interpolation = (InterpolationSyntax)content;
                            var value = BindValue(interpolation.Expression, diagnostics, BindValueKind.RValue);
                            if (value.Type is null)
                            {
                                value = GenerateConversionForAssignment(objectType, value, diagnostics);
                            }
                            else
                            {
                                value = BindToNaturalType(value, diagnostics);
                                _ = GenerateConversionForAssignment(objectType, value, diagnostics);
                            }

                            // We need to ensure the argument is not a lambda, method group, etc. It isn't nice to wait until lowering,
                            // when we perform overload resolution, to report a problem. So we do that check by calling
                            // GenerateConversionForAssignment with objectType. However we want to preserve the original expression's
                            // natural type so that overload resolution may select a specialized implementation of string.Format,
                            // so we discard the result of that call and only preserve its diagnostics.
                            BoundExpression? alignment = null;
                            BoundLiteral? format = null;
                            if (interpolation.AlignmentClause != null)
                            {
                                alignment = GenerateConversionForAssignment(intType, BindValue(interpolation.AlignmentClause.Value, diagnostics, Binder.BindValueKind.RValue), diagnostics);
                                var alignmentConstant = alignment.ConstantValue;
                                constantEnabled = false;
                                if (alignmentConstant != null && !alignmentConstant.IsBad)
                                {
                                    const int magnitudeLimit = 32767;
                                    // check that the magnitude of the alignment is "in range".
                                    int alignmentValue = alignmentConstant.Int32Value;
                                    //  We do the arithmetic using negative numbers because the largest negative int has no corresponding positive (absolute) value.
                                    alignmentValue = (alignmentValue > 0) ? -alignmentValue : alignmentValue;
                                    if (alignmentValue < -magnitudeLimit)
                                    {
                                        diagnostics.Add(ErrorCode.WRN_AlignmentMagnitude, alignment.Syntax.Location, alignmentConstant.Int32Value, magnitudeLimit);
                                    }
                                }
                                else if (!alignment.HasErrors)
                                {
                                    diagnostics.Add(ErrorCode.ERR_ConstantExpected, interpolation.AlignmentClause.Value.Location);
                                }
                            }

                            if (interpolation.FormatClause != null)
                            {
                                var text = interpolation.FormatClause.FormatStringToken.ValueText;
                                char lastChar;
                                bool hasErrors = false;
                                constantEnabled = false;
                                if (text.Length == 0)
                                {
                                    diagnostics.Add(ErrorCode.ERR_EmptyFormatSpecifier, interpolation.FormatClause.Location);
                                    hasErrors = true;
                                }
                                else if (SyntaxFacts.IsWhitespace(lastChar = text[text.Length - 1]) || SyntaxFacts.IsNewLine(lastChar))
                                {
                                    diagnostics.Add(ErrorCode.ERR_TrailingWhitespaceInFormatSpecifier, interpolation.FormatClause.Location);
                                    hasErrors = true;
                                }

                                format = new BoundLiteral(interpolation.FormatClause, ConstantValue.Create(text), stringType, hasErrors);
                            }

                            builder.Add(new BoundStringInsert(interpolation, value, alignment, format, null));
                            if (value.ConstantValue != null)
                            {
                                if (!value.ConstantValue.IsString || value.ConstantValue.IsBad || value.ConstantValue.IsNull)
                                {
                                    constantEnabled = false;
                                }

                                if (constantEnabled)
                                {
                                    resultConstant = FoldStringConcatenation(BinaryOperatorKind.StringConcatenation, (resultConstant ??= ConstantValue.Create(String.Empty, SpecialType.System_String)), value.ConstantValue);
                                }
                            }
                            else
                            {
                                constantEnabled = false;
                            }
                            continue;
                        }
                    case SyntaxKind.InterpolatedStringText:
                        {
                            var text = ((InterpolatedStringTextSyntax)content).TextToken.ValueText;
                            var constantVal = ConstantValue.Create(text, SpecialType.System_String);
                            builder.Add(new BoundLiteral(content, constantVal, stringType));
                            if (constantEnabled)
                            {
                                resultConstant = FoldStringConcatenation(BinaryOperatorKind.StringConcatenation, (resultConstant ??= ConstantValue.Create(String.Empty, SpecialType.System_String)), constantVal);
                            }
                            continue;
                        }
                    default:
                        throw ExceptionUtilities.UnexpectedValue(content.Kind());
                }
            }

            if (!constantEnabled)
            {
                resultConstant = null;
            }

            return new BoundInterpolatedString(node, builder.ToImmutableAndFree(), resultConstant, stringType);
        }
    }
}
