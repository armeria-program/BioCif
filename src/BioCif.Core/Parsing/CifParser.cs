﻿namespace BioCif.Core.Parsing
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;
    using Tokenization;
    using Tokenization.Tokens;

    /// <summary>
    /// Parses CIF files into the <see cref="Cif"/> data structure.
    /// </summary>
    public static class CifParser
    {
        /// <summary>
        /// Parse the CIF data from the input stream using provided options.
        /// </summary>
        public static Cif Parse(Stream stream, CifParsingOptions options = null)
        {
            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream));
            }

            ValidateStream(stream);

            if (options == null)
            {
                options = new CifParsingOptions();
            }
            else if (options.FileEncoding == null)
            {
                options.FileEncoding = Encoding.UTF8;
            }

            var blocks = new List<DataBlock>();

            var state = new Stack<ParsingState>(new[] { ParsingState.None });

            var previous = default(Token);
            var tokenBeforeNestedContext = default(Token);
            var lastName = default(DataName);

            var buffered = new BufferedStream(stream);
            using (var reader = new StreamReader(buffered, options.FileEncoding))
            {
                var activeBlock = default(DataBlockBuilder);
                var activeLoop = default(LoopBuilder);
                var listsStack = new Stack<List<IDataValue>>();
                var dictionariesStack = new Stack<(string name, Dictionary<string, IDataValue> values)>();

                foreach (var token in CifTokenizer.Tokenize(reader, options.Version))
                {
                    var currentState = state.Peek();

                    switch (token.TokenType)
                    {
                        case TokenType.Comment:
                            break;
                        case TokenType.DataBlock:
                            {
                                if (activeLoop != null)
                                {
                                    activeBlock.Members.Add(activeLoop.Build());
                                    state.Pop();
                                }

                                if (activeBlock != null)
                                {
                                    blocks.Add(activeBlock.Build());
                                    state.Pop();
                                }

                                state.Push(ParsingState.InsideDataBlock);

                                activeBlock = new DataBlockBuilder(token);
                            }
                            break;
                        case TokenType.Loop:
                            {
                                if (activeLoop != null)
                                {
                                    activeBlock.Members.Add(activeLoop.Build());
                                    state.Pop();
                                }

                                activeLoop = new LoopBuilder();

                                state.Push(ParsingState.InsideLoop);
                            }
                            break;
                        case TokenType.StartList:
                            {
                                tokenBeforeNestedContext = previous;
                                state.Push(ParsingState.InsideList);
                                listsStack.Push(new List<IDataValue>());
                            }
                            break;
                        case TokenType.EndList:
                            {
                                if (currentState != ParsingState.InsideList)
                                {
                                    throw new InvalidOperationException($"Encountered end of list token when in state {currentState}. Previous token was {previous}.");
                                }

                                state.Pop();
                                var completed = listsStack.Pop();
                                var list = new DataList(completed);

                                currentState = state.Peek();
                                switch (currentState)
                                {
                                    case ParsingState.InsideLoopValues:
                                        activeLoop.AddToRow(list);
                                        break;
                                    case ParsingState.InsideList:
                                        listsStack.Peek().Add(list);
                                        break;
                                    case ParsingState.InsideTable:
                                        dictionariesStack.Peek().values[tokenBeforeNestedContext.Value] = list;
                                        break;
                                    case ParsingState.InsideDataBlock:
                                        activeBlock.Members.Add(new DataItem(lastName, list));
                                        break;
                                    default:
                                        throw new InvalidOperationException($"List in unexpected context {completed} in {currentState}.");
                                }
                            }
                            break;
                        case TokenType.StartTable:
                            {
                                tokenBeforeNestedContext = previous;
                                state.Push(ParsingState.InsideTable);
                                dictionariesStack.Push((previous?.Value, new Dictionary<string, IDataValue>()));
                            }
                            break;
                        case TokenType.EndTable:
                            {
                                if (currentState != ParsingState.InsideTable)
                                {
                                    throw new InvalidOperationException($"Encountered end of table token when in state {currentState}. Previous token was {previous}.");
                                }

                                state.Pop();
                                var completed = dictionariesStack.Pop();
                                var dict = new DataDictionary(completed.values);
                                currentState = state.Peek();

                                switch (currentState)
                                {
                                    case ParsingState.InsideDataBlock:
                                        activeBlock.Members.Add(new DataItem(lastName, dict));
                                        break;
                                    case ParsingState.InsideLoop:
                                        activeLoop.AddToRow(dict);
                                        break;
                                    case ParsingState.InsideList:
                                        listsStack.Peek().Add(dict);
                                        break;
                                    case ParsingState.InsideTable:
                                        dictionariesStack.Peek().values[completed.name] = dict;
                                        break;
                                    default:
                                        throw new ArgumentOutOfRangeException();
                                }
                            }
                            break;
                        case TokenType.Value:
                            if (currentState == ParsingState.InsideDataBlock)
                            {
                                if (previous?.TokenType != TokenType.Name)
                                {
                                    throw new InvalidOperationException();
                                }

                                activeBlock.Members.Add(new DataItem(new DataName(previous.Value), new DataValueSimple(token.Value)));
                            }
                            else if (currentState == ParsingState.InsideLoop)
                            {
                                state.Pop();
                                state.Push(ParsingState.InsideLoopValues);
                                activeLoop.AddToRow(token);
                            }
                            else if (currentState == ParsingState.InsideLoopValues)
                            {
                                activeLoop.AddToRow(token);
                            }
                            else if (currentState == ParsingState.InsideList)
                            {
                                listsStack.Peek().Add(new DataValueSimple(token.Value));
                            }
                            break;
                        case TokenType.Name:
                            if (currentState == ParsingState.InsideLoop)
                            {
                                activeLoop.AddHeader(token);
                            }
                            else if (currentState == ParsingState.InsideLoopValues)
                            {
                                activeBlock.Members.Add(activeLoop.Build());
                                activeLoop = null;
                                state.Pop();
                            }
                            else
                            {
                                lastName = new DataName(token.Value);
                            }
                            break;
                        case TokenType.Unknown:
                            throw new InvalidOperationException($"Encountered unexpect token in CIF data: {token}.");

                    }

                    previous = token;
                }

                if (activeLoop != null)
                {
                    activeBlock.Members.Add(activeLoop.Build());
                }

                if (activeBlock != null)
                {
                    blocks.Add(activeBlock.Build());
                }
            }

            return new Cif(blocks);
        }

        private static void ValidateStream(Stream stream)
        {
            if (!stream.CanRead)
            {
                throw new ArgumentException($"Could not read from the provided stream of type {stream.GetType().FullName}.");
            }

            if (!stream.CanSeek)
            {
                throw new ArgumentException($"Could not seek in provided stream of type {stream.GetType().FullName}.");
            }
        }

        private class DataBlockBuilder
        {
            public Token Token { get; }

            public List<IDataBlockMember> Members { get; } = new List<IDataBlockMember>();

            public DataBlockBuilder(Token token)
            {
                if (token == null)
                {
                    throw new ArgumentNullException(nameof(token));
                }

                if (token.TokenType != TokenType.DataBlock)
                {
                    throw new ArgumentException($"Invalid token to start data block: {token}.", nameof(token));
                }

                Token = token;
            }

            public DataBlock Build()
            {
                return new DataBlock(Token.Value.Substring(5), Members);
            }
        }

        private class LoopBuilder
        {
            private readonly List<string> headers = new List<string>();

            private readonly List<List<IDataValue>> values = new List<List<IDataValue>>();

            public void AddHeader(Token token)
            {
                if (token == null)
                {
                    throw new ArgumentNullException(nameof(token));
                }

                if (token.TokenType != TokenType.Name)
                {
                    throw new InvalidOperationException($"Tried to set a header for a loop with type: {token}.");
                }

                headers.Add(token.Value);
            }

            public void AddToRow(Token token)
            {
                if (headers.Count == 0)
                {
                    throw new InvalidOperationException($"Attempted to add token to table with empty headers: {token}.");
                }

                if (values.Count == 0)
                {
                    values.Add(new List<IDataValue> { new DataValueSimple(token.Value) });
                }
                else
                {
                    var last = values[values.Count - 1];

                    if (last.Count < headers.Count)
                    {
                        last.Add(new DataValueSimple(token.Value));
                    }
                    else
                    {
                        values.Add(new List<IDataValue> { new DataValueSimple(token.Value) });
                    }
                }
            }

            public void AddToRow(IDataValue value)
            {
                if (headers.Count == 0)
                {
                    throw new InvalidOperationException($"Attempted to add value to table with empty headers: {value}.");
                }

                if (values.Count == 0)
                {
                    values.Add(new List<IDataValue> { value });
                }
                else
                {
                    var last = values[values.Count - 1];

                    if (last.Count < headers.Count)
                    {
                        last.Add(value);
                    }
                    else
                    {
                        values.Add(new List<IDataValue> { value });
                    }
                }
            }

            public Table Build()
            {
                var names = headers.Select(x => new DataName(x)).ToList();

                var rows = values.Select(x => new TableRow
                {
                    Values = x
                }).ToList();

                return new Table(names, rows);
            }
        }

        public enum ParsingState
        {
            None = 0,
            InsideDataBlock = 1,
            InsideLoop = 2,
            InsideList = 3,
            InsideTable = 4,
            InsideSaveFrame = 5,
            InsideLoopValues = 6
        }
    }
}