﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Squirrel.Nodes;

namespace Squirrel
{
    public class Evaluator
    {
        private readonly INode _root;
        private readonly List<string> _includeDirs;

        private delegate INode BuiltinFunctionDelegate(List<INode> args, Environment env);
        private Dictionary<string, BuiltinFunctionDelegate> _builtinFunctions;

        private void InitializeBuiltinFunctionDictionary()
        {
            _builtinFunctions = new Dictionary<string, BuiltinFunctionDelegate>
            {
                {"add", BuiltinAdd},
                {"block", BuiltinBlock},
                {"def", BuiltinDef},
                {"display", BuiltinDisplay},
                {"div", BuiltinDiv},
                {"eq", BuiltinEq},
                {"gt", BuiltinGt},
                {"id", BuiltinId},
                {"if", BuiltinIf},
                {"include", BuiltinInclude},
                {"join", BuiltinJoin},
                {"lambda", BuiltinLambda},
                {"len", BuiltinLen},
                {"lt", BuiltinLt},
                {"mod", BuiltinMod},
                {"module", BuiltinModule},
                {"mul", BuiltinMul},
                {"nth", BuiltinNth},
                {"outer", BuiltinOuter},
                {"print", BuiltinPrint},
                {"quote", BuiltinQuote},
                {"set", BuiltinSet},
                {"slice", BuiltinSlice},
                {"sub", BuiltinSub},
                {"unquote", BuiltinUnquote}
            };
        }

        public static readonly INode True = new SymbolNode("true");
        public static readonly INode False = new SymbolNode("false");
        public static readonly INode Null = new SymbolNode("null");

        public Evaluator(INode root, List<string> includeDirs)
        {
            _root = root;
            _includeDirs = includeDirs;
            InitializeBuiltinFunctionDictionary();
        }

        public Evaluator(INode root) : this(root, new List<string>())
        {
        }

        public INode Evaluate() => VisitNode(_root, new Environment());

        public INode Evaluate(ref Environment env) => VisitNode(_root, env);

        private INode VisitNode(INode node, Environment env)
        {
            var methodName = $"Visit{node.GetType().Name}";
            var method = GetType().GetTypeInfo().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
            return (INode)method.Invoke(this, new object[] { node, new Environment(env) });
        }

        private INode VisitIntegerNode(IntegerNode node, Environment env) => node;

        private INode VisitSymbolNode(SymbolNode node, Environment env)
        {
            while (true)
            {
                var nullable = env.Get(node.Value);

                if (nullable == null)
                {
                    return node;
                }

                if (nullable.GetType() != typeof(SymbolNode))
                {
                    return nullable;
                }

                node = (SymbolNode)nullable;
            }
        }

        private INode VisitStringNode(StringNode node, Environment env) => node;

        private INode VisitSymbolicExpressionNode(SymbolicExpressionNode node, Environment env)
        {
            if (node.Children.Count == 0)
            {
                return new ErrorNode("symbolic expression cannot be empty");
            }

            var visitedChildren = new List<INode>();
            foreach (var child in node.Children)
            {
                var visitedChild = VisitNode(child, env);
                if (visitedChild.GetType() == typeof(ErrorNode))
                {
                    return visitedChild;
                }
                visitedChildren.Add(visitedChild);
            }

            var head = visitedChildren.Head();
            var tail = visitedChildren.Tail();

            if (head.GetType() == typeof(SymbolNode))
            {
                return EvaluateBuiltinFunction((SymbolNode)head, tail, env);
            }

            if (head.GetType() == typeof(LambdaFunctionNode))
            {
                return EvaluateLambdaFunction((LambdaFunctionNode)head, tail, env);
            }

            return new ErrorNode("first element of symbolic expression must be a symbol or lambda function");
        }

        private INode VisitQuotedExpressionNode(QuotedExpressionNode node, Environment env) => node;

        private INode VisitLambdaFunctionNode(LambdaFunctionNode node, Environment env) => node;

        private INode VisitErrorNode(ErrorNode node, Environment env) => node;

        private INode EvaluateBuiltinFunction(SymbolNode head, List<INode> tail, Environment env)
        {
            var functionName = head.Value;

            if (!_builtinFunctions.ContainsKey(functionName))
            {
                return new ErrorNode($"function is not defined: {functionName}");
            }

            var function = _builtinFunctions[functionName];

            var expectedTypeAttr = function.GetMethodInfo().GetCustomAttribute<ExpectedTypeAttribute>();

            if (expectedTypeAttr != null)
            {
                var expectedType = expectedTypeAttr.ExpectedType;
                foreach (var arg in tail)
                {
                    var actualType = arg.GetType();
                    if (actualType != expectedType)
                    {
                        return new ErrorNode($"expected argument of type {expectedType} but got type {actualType}");
                    }
                }
            }

            var expectedTypesAttr = function.GetMethodInfo().GetCustomAttribute<ExpectedTypesAttribute>();

            if (expectedTypesAttr != null)
            {
                var expectedTypes = expectedTypesAttr.ExpectedTypes;

                var expectedCount = expectedTypes.Length;
                var actualCount = tail.Count;

                if (actualCount != expectedCount)
                {
                    return new ErrorNode($"function takes exactly {expectedCount} arguments ({actualCount} given)");
                }

                for (var i = 0; i < expectedCount; i++)
                {
                    var expectedType = expectedTypes[i];
                    var actualType = tail[i].GetType();

                    if (expectedType == typeof(INode))
                    {
                        continue;
                    }

                    if (actualType != expectedType)
                    {
                        return new ErrorNode($"expected argument of type {expectedType} but got type {actualType}");
                    }
                }
            }

            return function.Invoke(tail, env);
        }

        private INode EvaluateLambdaFunction(LambdaFunctionNode head, List<INode> tail, Environment env)
        {
            var expectedCount = head.Parameters.Children.Count;
            var actualCount = tail.Count;

            if (actualCount != expectedCount)
            {
                return new ErrorNode($"function takes exactly {expectedCount} arguments ({actualCount} given)");
            }

            for (var i = 0; i < expectedCount; i++)
            {
                var key = ((SymbolNode)head.Parameters.Children[i]).Value;
                var value = tail[i];
                env.Put(key, value);
            }

            return BuiltinUnquote(new List<INode> { head.Body }, env);
        }

        [ExpectedType(typeof(IntegerNode))]
        private INode BuiltinAdd(List<INode> args, Environment env)
        {
            if (args.Count < 2)
            {
                return new ErrorNode($"function takes at least 2 arguments ({args.Count} given)");
            }
            var sum = args.Sum(arg => ((IntegerNode)arg).Value);
            return new IntegerNode(sum);
        }

        private INode BuiltinBlock(List<INode> args, Environment env) => args[args.Count - 1];

        private INode BuiltinDef(List<INode> args, Environment env)
        {
            if (args.Count < 2)
            {
                return new ErrorNode($"function takes at least 2 arguments ({args.Count} given)");
            }

            var names = ((QuotedExpressionNode)(args.Head())).Children;

            if (names.Any(name => name.GetType() != typeof(SymbolNode)))
            {
                return new ErrorNode($"names must be of type {nameof(SymbolNode)}");
            }

            var values = args.Tail();

            if (names.Count != values.Count)
            {
                return new ErrorNode(
                    $"number of values ({values.Count}) must equal number of names ({names.Count})");
            }

            for (var i = 0; i < names.Count; i++)
            {
                var name = ((SymbolNode)names[i]).Value;
                var value = values[i];

                if (_builtinFunctions.ContainsKey(name))
                {
                    return new ErrorNode($"cannot redefine builtin function: {name}");
                }

                env.PutParent(name, value);
            }

            return Null;
        }

        [ExpectedTypes(typeof(INode))]
        private INode BuiltinDisplay(List<INode> args, Environment env)
        {
            Console.WriteLine(args[0]);
            return Null;
        }

        [ExpectedTypes(typeof(IntegerNode), typeof(IntegerNode))]
        private INode BuiltinDiv(List<INode> args, Environment env)
        {
            var first = ((IntegerNode)args[0]).Value;
            var second = ((IntegerNode)args[1]).Value;

            if (second == 0)
            {
                return new ErrorNode("cannot divide by zero");
            }

            var quotient = first / second;
            return new IntegerNode(quotient);
        }

        [ExpectedTypes(typeof(INode), typeof(INode))]
        private INode BuiltinEq(List<INode> args, Environment env) => args[0].Equals(args[1]) ? True : False;

        [ExpectedTypes(typeof(IntegerNode), typeof(IntegerNode))]
        private INode BuiltinGt(List<INode> args, Environment env)
        {
            var first = ((IntegerNode)args[0]).Value;
            var second = ((IntegerNode)args[1]).Value;
            return first > second ? True : False;
        }

        [ExpectedTypes(typeof(INode))]
        private INode BuiltinId(List<INode> args, Environment env) => args[0];

        [ExpectedTypes(typeof(SymbolNode), typeof(QuotedExpressionNode), typeof(QuotedExpressionNode))]
        private INode BuiltinIf(List<INode> args, Environment env)
        {
            var condition = (SymbolNode)args[0];
            var resultIfTrue = (QuotedExpressionNode)args[1];
            var resultIfFalse = (QuotedExpressionNode)args[2];
            QuotedExpressionNode actualResult;

            if (condition.Equals(True))
            {
                actualResult = resultIfTrue;
            }
            else if (condition.Equals(False))
            {
                actualResult = resultIfFalse;
            }
            else
            {
                return new ErrorNode($"condition must evaluate to true or false, but evaluated to: {condition}");
            }

            return BuiltinUnquote(new List<INode> { actualResult }, env);
        }

        [ExpectedTypes(typeof(StringNode))]
        private INode BuiltinInclude(List<INode> args, Environment env)
        {
            var moduleName = ((StringNode)args[0]).Value;

            string input = null;
            foreach (var dir in _includeDirs)
            {
                var path = Path.Combine(dir, moduleName + ".sq");
                if (File.Exists(path))
                {
                    input = File.ReadAllText(path);
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(input))
            {
                return new ErrorNode($"module not found: {moduleName}");
            }

            var tokenizer = new Tokenizer(input);
            var tokens = tokenizer.Tokenize();
            var parser = new Parser(tokens);
            var evaluator = new Evaluator(parser.Parse(), _includeDirs);

            var moduleEnv = new Environment();
            var result = evaluator.Evaluate(ref moduleEnv);
            if (result.GetType() == typeof(ErrorNode))
            {
                return result;
            }
            env.Parent.Extend(moduleEnv);

            return Null;
        }

        [ExpectedType(typeof(QuotedExpressionNode))]
        private INode BuiltinJoin(List<INode> args, Environment env)
        {
            var joined = new List<INode>();
            foreach (var arg in args)
            {
                joined.AddRange(((QuotedExpressionNode)arg).Children);
            }
            return new QuotedExpressionNode(joined);
        }

        [ExpectedTypes(typeof(QuotedExpressionNode), typeof(QuotedExpressionNode))]
        private INode BuiltinLambda(List<INode> args, Environment env)
        {
            var parameters = (QuotedExpressionNode)args[0];
            if (parameters.Children.Any(node => !(node is SymbolNode)))
            {
                return new ErrorNode("list of lambda function parameters must contain only symbols");
            }

            var body = (QuotedExpressionNode)args[1];

            return new LambdaFunctionNode(parameters, body);
        }

        [ExpectedTypes(typeof(QuotedExpressionNode))]
        private INode BuiltinLen(List<INode> args, Environment env)
        {
            return new IntegerNode(((QuotedExpressionNode)args[0]).Children.Count);
        }

        [ExpectedTypes(typeof(IntegerNode), typeof(IntegerNode))]
        private INode BuiltinLt(List<INode> args, Environment env)
        {
            var first = ((IntegerNode)args[0]).Value;
            var second = ((IntegerNode)args[1]).Value;
            return first < second ? True : False;
        }

        [ExpectedTypes(typeof(IntegerNode), typeof(IntegerNode))]
        private INode BuiltinMod(List<INode> args, Environment env)
        {
            var first = ((IntegerNode)args[0]).Value;
            var second = ((IntegerNode)args[1]).Value;

            if (second == 0)
            {
                return new ErrorNode("cannot divide by zero");
            }

            var remainder = first % second;
            return new IntegerNode(remainder);
        }

        private INode BuiltinModule(List<INode> args, Environment env)
        {
            env.Parent.Extend(env);
            return Null;
        }

        [ExpectedType(typeof(IntegerNode))]
        private INode BuiltinMul(List<INode> args, Environment env)
        {
            if (args.Count < 2)
            {
                return new ErrorNode($"function takes exactly 2 arguments ({args.Count} given)");
            }
            var product = args.Aggregate(1, (current, arg) => current * ((IntegerNode)arg).Value);
            return new IntegerNode(product);
        }

        [ExpectedTypes(typeof(QuotedExpressionNode), typeof(IntegerNode))]
        private INode BuiltinNth(List<INode> args, Environment env)
        {
            var list = ((QuotedExpressionNode)args[0]).Children;
            var n = ((IntegerNode)args[1]).Value;

            if (n < 1)
            {
                return new ErrorNode($"n ({n}) must be greater than 0");
            }

            if (n > list.Count)
            {
                return new ErrorNode($"n ({n}) must not be greater than the length of the list ({list.Count})");
            }

            return list[n - 1];
        }

        private INode BuiltinOuter(List<INode> args, Environment env)
        {
            if (args.Count < 2)
            {
                return new ErrorNode($"function takes at least 2 arguments ({args.Count} given)");
            }

            var names = ((QuotedExpressionNode)(args.Head())).Children;

            if (names.Any(name => name.GetType() != typeof(SymbolNode)))
            {
                return new ErrorNode($"names must be of type {nameof(SymbolNode)}");
            }

            var values = args.Tail();

            if (names.Count != values.Count)
            {
                return new ErrorNode(
                    $"number of values ({values.Count}) must equal number of names ({names.Count})");
            }

            for (var i = 0; i < names.Count; i++)
            {
                var name = ((SymbolNode)names[i]).Value;
                var value = values[i];

                if (_builtinFunctions.ContainsKey(name))
                {
                    return new ErrorNode($"cannot redefine builtin function: {name}");
                }

                env.PutGrandparent(name, value);
            }

            return Null;
        }

        [ExpectedTypes(typeof(StringNode))]
        private INode BuiltinPrint(List<INode> args, Environment env)
        {
            Console.Write(((StringNode)args[0]).Value);
            return Null;
        }

        private INode BuiltinQuote(List<INode> args, Environment env)
        {
            return new QuotedExpressionNode(args);
        }

        [ExpectedTypes(typeof(QuotedExpressionNode), typeof(IntegerNode), typeof(INode))]
        private INode BuiltinSet(List<INode> args, Environment env)
        {
            var first = (QuotedExpressionNode)args[0];
            var second = (IntegerNode)args[1];
            var third = args[2];

            if (first.Children.Count != 1)
            {
                return new ErrorNode("expected singleton array");
            }

            var child = first.Children[0];
            if (child.GetType() != typeof(SymbolNode))
            {
                return new ErrorNode("expected singleton array to contain a symbol");
            }

            var definition = env.Get(((SymbolNode)child).Value);
            if (definition?.GetType() != typeof(QuotedExpressionNode))
            {
                return new ErrorNode("symbol must be bound to an array");
            }

            var array = (QuotedExpressionNode)definition;
            var index = second.Value;
            var replacement = third;

            if (index < 0 || index >= array.Children.Count)
            {
                return new ErrorNode("index is out of bounds");
            }

            array.Children[index] = replacement;

            return Null;
        }

        [ExpectedTypes(typeof(QuotedExpressionNode), typeof(IntegerNode), typeof(IntegerNode))]
        private INode BuiltinSlice(List<INode> args, Environment env)
        {
            var elements = ((QuotedExpressionNode)args[0]).Children;
            var begin = ((IntegerNode)args[1]).Value;
            var end = ((IntegerNode)args[2]).Value;
            var length = elements.Count;

            if (begin < 0 || begin > length)
            {
                return new ErrorNode($"begin index out of range: {begin}");
            }

            if (end < 0 || end > length)
            {
                return new ErrorNode($"end index out of range: {end}");
            }

            if (begin > end)
            {
                return new ErrorNode($"end index must be greater than start index: " +
                                     $"{nameof(begin)} = {begin}, {nameof(end)} = {end}");
            }

            var sliced = elements.GetRange(begin, (end - begin));
            return new QuotedExpressionNode(sliced);
        }

        [ExpectedTypes(typeof(IntegerNode), typeof(IntegerNode))]
        private INode BuiltinSub(List<INode> args, Environment env)
        {
            var first = ((IntegerNode)args[0]).Value;
            var second = ((IntegerNode)args[1]).Value;
            var difference = first - second;
            return new IntegerNode(difference);
        }

        [ExpectedTypes(typeof(QuotedExpressionNode))]
        private INode BuiltinUnquote(List<INode> args, Environment env)
        {
            var children = ((QuotedExpressionNode)args[0]).Children;
            return VisitNode(new SymbolicExpressionNode(children), env);
        }
    }
}
