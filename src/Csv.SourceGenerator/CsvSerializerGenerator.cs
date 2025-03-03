﻿using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Csv.SourceGenerator;

[Generator(LanguageNames.CSharp)]
public partial class CsvSerializerGenerator : IIncrementalGenerator
{
	public void Initialize(IncrementalGeneratorInitializationContext context)
	{
		var provider = context.SyntaxProvider
			.ForAttributeWithMetadataName(
				"Csv.Annotations.CsvObjectAttribute",
				static (node, cancellation) =>
				{
					return node is ClassDeclarationSyntax
						or StructDeclarationSyntax
						or RecordDeclarationSyntax;
				},
				static (context, cancellation) => { return context; })
			.Combine(context.CompilationProvider)
			.WithComparer(Comparer.Instance);

		context.RegisterSourceOutput(
			context.CompilationProvider.Combine(provider.Collect()),
			(sourceProductionContext, t) =>
			{
				var (compilation, list) = t;
				var references = ReferenceSymbols.Create(compilation);
				if (references == null) return;

				var builder = new CodeBuilder();

				var serializerTargetTypes = new List<TypeMetadata>();

				foreach (var (x, _) in list)
				{
					var typeMeta = new TypeMetadata(
						(TypeDeclarationSyntax)x.TargetNode,
						(INamedTypeSymbol)x.TargetSymbol,
						references);

					if (TryEmit(typeMeta, builder, in sourceProductionContext))
					{
						var fullType = typeMeta.FullTypeName
							.Replace("global::", "")
							.Replace("<", "_")
							.Replace(">", "_");

						sourceProductionContext.AddSource($"{fullType}.CsvSerializer.g.cs", builder.ToString());
						serializerTargetTypes.Add(typeMeta);
					}
					builder.Clear();
				}

				// EmitProvider(serializerTargetTypes, in sourceProductionContext);
			});
	}

	static bool TryEmit(
		TypeMetadata typeMetadata,
		CodeBuilder builder,
		in SourceProductionContext context)
	{
		try
		{
			var error = false;

			// must be partial
			if (!typeMetadata.IsPartial())
			{
				context.ReportDiagnostic(Diagnostic.Create(
					DiagnosticDescriptors.MustBePartial,
					typeMetadata.Syntax.Identifier.GetLocation(),
					typeMetadata.Symbol.Name));
				error = true;
			}

			// nested is not allowed
			if (typeMetadata.IsNested())
			{
				context.ReportDiagnostic(Diagnostic.Create(
					DiagnosticDescriptors.NestedNotAllowed,
					typeMetadata.Syntax.Identifier.GetLocation(),
					typeMetadata.Symbol.Name));
				error = true;
			}

			// verify abstract/interface
			if (typeMetadata.Symbol.IsAbstract)
			{
				context.ReportDiagnostic(Diagnostic.Create(
					DiagnosticDescriptors.AbstractNotAllowed,
					typeMetadata.Syntax.Identifier.GetLocation(),
					typeMetadata.TypeName));
				error = true;
			}

			if (error)
			{
				return false;
			}

			builder.AppendLine("// <auto-generated />");
			builder.AppendLine("#nullable enable");
			builder.AppendLine("#pragma warning disable CS0162 // Unreachable code");
			builder.AppendLine("#pragma warning disable CS0219 // Variable assigned but never used");
			builder.AppendLine("#pragma warning disable CS8600 // Converting null literal or possible null value to non-nullable type.");
			builder.AppendLine("#pragma warning disable CS8601 // Possible null reference assignment");
			builder.AppendLine("#pragma warning disable CS8602 // Possible null return");
			builder.AppendLine("#pragma warning disable CS8604 // Possible null reference argument for parameter");
			builder.AppendLine("#pragma warning disable CS8631 // The type cannot be used as type parameter in the generic type or method");
			builder.AppendLine();
			builder.AppendLine("using System;");
			builder.AppendLine("using Csv;");
			builder.AppendLine("using Csv.Annotations;");
			builder.AppendLine("using Csv.Internal;");
			builder.AppendLine();

			var ns = typeMetadata.Symbol.ContainingNamespace;
			if (!ns.IsGlobalNamespace)
			{
				builder.AppendLine($"namespace {ns}");
				builder.BeginBlock();
			}

			var typeDeclarationKeyword = (typeMetadata.Symbol.IsRecord, typeMetadata.Symbol.IsValueType) switch
			{
				(true, true) => "record struct",
				(true, false) => "record",
				(false, true) => "struct",
				(false, false) => "class",
			};

			using var _ = builder.BeginBlockScope($"partial {typeDeclarationKeyword} {typeMetadata.TypeName} : global::Csv.ICsvSerializerRegister");

			if (!TryEmitRegisterMethod(typeMetadata, builder))
			{
				return false;
			}

			if (!TryEmitSerializer(typeMetadata, builder))
			{
				return false;
			}

			if (!ns.IsGlobalNamespace) builder.EndBlock();

			builder.AppendLine("#pragma warning restore CS0162 // Unreachable code");
			builder.AppendLine("#pragma warning restore CS0219 // Variable assigned but never used");
			builder.AppendLine("#pragma warning restore CS8600 // Converting null literal or possible null value to non-nullable type.");
			builder.AppendLine("#pragma warning restore CS8601 // Possible null reference assignment");
			builder.AppendLine("#pragma warning restore CS8602 // Possible null return");
			builder.AppendLine("#pragma warning restore CS8604 // Possible null reference argument for parameter");
			builder.AppendLine("#pragma warning restore CS8631 // The type cannot be used as type parameter in the generic type or method");
			return true;
		}
		catch (Exception)
		{
			return false;
		}
	}

	static bool TryEmitRegisterMethod(TypeMetadata type, CodeBuilder builder)
	{
		using var _ = builder.BeginBlockScope("static void RegisterCsvSerializer()");
		builder.AppendLine($"global::Csv.CsvSerializer.Register(GeneratedCsvSerializer.Instance);");
		return true;
	}

	static bool TryEmitSerializer(TypeMetadata type, CodeBuilder builder)
	{
		using var _ = builder.BeginBlockScope($"class GeneratedCsvSerializer : ICsvSerializer<{type.FullTypeName}>");

		builder.AppendLine($"public static readonly GeneratedCsvSerializer Instance = new();");

		EmitUtf8KeyCache(type, builder);
		builder.AppendLine();
		EmitSerializeMethod_Span(type, builder);
		builder.AppendLine();
		EmitSerializeMethod_Enumerable(type, builder);
		builder.AppendLine();

		if (type.KeyAsPropertyName || (type.Members.Count > 0 && type.Members[0].IsStringKey))
		{
			EmitDeserializeMethod_StringKey(type, builder);
		}
		else
		{
			EmitDeserializeMethod_IntKey(type, builder);
		}

		return true;
	}

	static void EmitUtf8KeyCache(TypeMetadata type, CodeBuilder builder)
	{
		foreach (var memberMeta in type.Members)
		{
			builder.Append($"static readonly byte[] {memberMeta.Symbol.Name}Utf8Key = ");
			builder.AppendByteArrayString(memberMeta.Utf8Key);
			builder.AppendLine($"; // {memberMeta.Key}", false);
		}
	}

	static void EmitSerializeMethod_Span(TypeMetadata typeMetadata, CodeBuilder builder)
	{
		var members = typeMetadata.Members;

		using var methodScope = builder.BeginBlockScope(
			$"public void Serialize(ref global::Csv.CsvWriter writer, global::System.ReadOnlySpan<{typeMetadata.FullTypeName}> values)"
		);

		EmitWriteHeader(typeMetadata, builder);

		using (var writeScope = builder.BeginBlockScope("for (int i = 0; i < values.Length; i++)"))
		{
			builder.AppendLine("var item = values[i];");

			for (int i = 0; i < members.Count; i++)
			{
				EmitWriteMember(members[i], builder);
			
				if (i == members.Count - 1)
				{
					builder.AppendLine("if (i != values.Length - 1) writer.WriteEndOfLine();");
				}
				else
				{
					builder.AppendLine("writer.WriteSeparator();");
				}
			}
		}
	}

	static void EmitSerializeMethod_Enumerable(TypeMetadata typeMetadata, CodeBuilder builder)
	{
		var members = typeMetadata.Members;

		using var methodScope = builder.BeginBlockScope(
			$"public void Serialize(ref global::Csv.CsvWriter writer, global::System.Collections.Generic.IEnumerable<{typeMetadata.FullTypeName}> values)"
		);

		EmitWriteHeader(typeMetadata, builder);

		builder.AppendLine("var e = values.GetEnumerator();");
		using (builder.BeginBlockScope("try"))
		{
			builder.AppendLine("if (!e.MoveNext()) return;");

			using (builder.BeginBlockScope("while (true)"))
			{
				builder.AppendLine("var item = e.Current;");

				for (int i = 0; i < members.Count; i++)
				{
					EmitWriteMember(members[i], builder);

					if (i == members.Count - 1)
					{
						using (builder.BeginBlockScope("if (!e.MoveNext())"))
						{
							builder.AppendLine("writer.WriteEndOfLine();");
							builder.AppendLine("break;");
						}
					}
					else
					{
						builder.AppendLine("writer.WriteSeparator();");
					}
				}
			}
		}
		using (builder.BeginBlockScope("finally"))
		{
			builder.AppendLine("e.Dispose();");
		}
	}

	static void EmitWriteHeader(TypeMetadata type, CodeBuilder builder)
	{
		var members = type.Members;

		using (var headerScope = builder.BeginBlockScope("if (writer.Options.HasHeader)"))
		{
			builder.AppendLine("var quoteHeader = writer.Options.QuoteMode is (global::Csv.QuoteMode.All or global::Csv.QuoteMode.NonNumeric);");

			for (int i = 0; i < members.Count; i++)
			{
				var memberMeta = members[i];

				builder.AppendLine($"if (quoteHeader) writer.WriteRaw((byte)'\"');");
				builder.AppendLine($"writer.WriteRaw({memberMeta.Symbol.Name}Utf8Key.AsSpan());");
				builder.AppendLine($"if (quoteHeader) writer.WriteRaw((byte)'\"');");

				if (i == members.Count - 1)
				{
					builder.AppendLine("writer.WriteEndOfLine();");
				}
				else
				{
					builder.AppendLine("writer.WriteSeparator();");
				}
			}
		}
	}

	static void EmitWriteMember(MemberMetadata member, CodeBuilder builder)
	{
		switch (member.FullTypeName)
		{
			case "System.Boolean":
			case "bool":
				builder.AppendLine($"writer.WriteBoolean(item.{member.Symbol.Name});");
				break;
			case "System.SByte":
			case "sbyte":
				builder.AppendLine($"writer.WriteSByte(item.{member.Symbol.Name});");
				break;
			case "System.Byte":
			case "byte":
				builder.AppendLine($"writer.WriteByte(item.{member.Symbol.Name});");
				break;
			case "System.Int16":
			case "short":
				builder.AppendLine($"writer.WriteInt16(item.{member.Symbol.Name});");
				break;
			case "System.Int32":
			case "int":
				builder.AppendLine($"writer.WriteInt32(item.{member.Symbol.Name});");
				break;
			case "System.Int64":
			case "long":
				builder.AppendLine($"writer.WriteInt64(item.{member.Symbol.Name});");
				break;
			case "System.UInt16":
			case "ushort":
				builder.AppendLine($"writer.WriteUInt16(item.{member.Symbol.Name});");
				break;
			case "System.UInt32":
			case "uint":
				builder.AppendLine($"writer.WriteUInt32(item.{member.Symbol.Name});");
				break;
			case "System.UInt64":
			case "ulong":
				builder.AppendLine($"writer.WriteUInt64(item.{member.Symbol.Name});");
				break;
			case "System.Single":
			case "float":
				builder.AppendLine($"writer.WriteSingle(item.{member.Symbol.Name});");
				break;
			case "System.Double":
			case "double":
				builder.AppendLine($"writer.WriteDouble(item.{member.Symbol.Name});");
				break;
			case "System.Decimal":
			case "decimal":
				builder.AppendLine($"writer.WriteDecimal(item.{member.Symbol.Name});");
				break;
			case "System.String":
			case "string":
				builder.AppendLine($"writer.WriteString(item.{member.Symbol.Name});");
				break;
			case "System.Char":
			case "char":
				builder.AppendLine($"writer.WriteChar(item.{member.Symbol.Name});");
				break;
			default:
				builder.AppendLine($"writer.Options.FormatterProvider.GetFormatter<{member.FullTypeName}>().Serialize(ref writer, item.{member.Symbol.Name});");
				break;
		}
	}

	static void EmitDeserializeMethod_IntKey(TypeMetadata type, CodeBuilder builder)
	{
		void EmitReadHeader()
		{
			builder.AppendLine("var allowComments = reader.Options.AllowComments;");
			builder.AppendLine("while (reader.TryReadEndOfLine(true) || (allowComments && reader.TrySkipComment(false))) { }");
			builder.AppendLine("if (reader.Options.HasHeader) reader.SkipLine();");
		}

		var members = type.Members;

		using (builder.BeginBlockScope($"public {type.FullTypeName}[] Deserialize(ref global::Csv.CsvReader reader)"))
		{
			EmitReadHeader();

			builder.AppendLine($"using var list = new TempList<{type.FullTypeName}>();");

			using (builder.BeginBlockScope("while (reader.Remaining > 0)"))
			{
				builder.AppendLine("if (reader.TryReadEndOfLine()) continue;");
				builder.AppendLine("if (allowComments && reader.TrySkipComment(false)) continue;");

				EmitDeserializeLocalVariables(type, builder);
				builder.AppendLine("var ___endOfLine = false;");

				using (builder.BeginBlockScope($"for (int __i = 0; __i <= {members.Max(x => x.Index)}; __i++)"))
				{	
					using (builder.BeginBlockScope("switch (__i)"))
					{
						foreach (var member in members)
						{
							using (builder.BeginIndentScope($"case {member.Index}:"))
							{
								EmitReadMember(member, builder);
								builder.AppendLine("break;");
							}
						}
						using (builder.BeginIndentScope("default:"))
						{
							builder.AppendLine("reader.SkipField();");
							builder.AppendLine("break;");
						}
					}

					using (builder.BeginBlockScope("if (reader.TryReadEndOfLine(true))"))
					{
						builder.AppendLine("___endOfLine = true;");
						builder.AppendLine("goto ADD_ITEM;");
					}

					builder.AppendLine("if (!reader.TryReadSeparator(false)) goto ADD_ITEM;");
				}

				builder.AppendLine();
				builder.AppendLine("ADD_ITEM:");
				builder.Append("list.Add(");
				using (builder.BeginBlockScope("new()"))
				{
					foreach (var member in type.Members)
					{
						builder.AppendLine($"{member.Symbol.Name} = __{member.Symbol.Name},");
					}
				}
				builder.AppendLine(");");

				builder.AppendLine();
				builder.AppendLine("if (!___endOfLine) reader.SkipLine();");
			}

			builder.AppendLine("return list.AsSpan().ToArray();");
		}

		builder.AppendLine();

		using (builder.BeginBlockScope($"public int Deserialize(ref global::Csv.CsvReader reader, global::System.Span<{type.FullTypeName}> destination)"))
		{
			EmitReadHeader();

			builder.AppendLine("var n = 0;");
			using (builder.BeginBlockScope("while (reader.Remaining > 0)"))
			{
				builder.AppendLine("if (reader.TryReadEndOfLine()) continue;");
				builder.AppendLine("if (allowComments && reader.TrySkipComment(false)) continue;");

				EmitDeserializeLocalVariables(type, builder);
				builder.AppendLine("var ___endOfLine = false;");

				using (builder.BeginBlockScope($"for (int __i = 0; __i <= {members.Max(x => x.Index)}; __i++)"))
				{
					using (builder.BeginBlockScope("switch (__i)"))
					{
						foreach (var member in members)
						{
							using (builder.BeginIndentScope($"case {member.Index}:"))
							{
								EmitReadMember(member, builder);
								builder.AppendLine("break;");
							}
						}
						using (builder.BeginIndentScope("default:"))
						{
							builder.AppendLine("reader.SkipField();");
							builder.AppendLine("break;");
						}
					}

					using (builder.BeginBlockScope("if (reader.TryReadEndOfLine(true))"))
					{
						builder.AppendLine("___endOfLine = true;");
						builder.AppendLine("goto ADD_ITEM;");
					}

					builder.AppendLine("if (!reader.TryReadSeparator(false)) goto ADD_ITEM;");
				}

				builder.AppendLine();
				builder.AppendLine("ADD_ITEM:");
				using (builder.BeginBlockScope("destination[n++] = new()"))
				{
					foreach (var member in type.Members)
					{
						builder.AppendLine($"{member.Symbol.Name} = __{member.Symbol.Name},");
					}
				}
				builder.AppendLine(";");

				builder.AppendLine();
				builder.AppendLine("if (!___endOfLine) reader.SkipLine();");
			}

			builder.AppendLine("return n;");
		}
	}

	static void EmitDeserializeMethod_StringKey(TypeMetadata type, CodeBuilder builder)
	{
		EmitGetColumnIndexMethod(type, builder);
		
		var members = type.Members;

		void EmitReadHeader()
		{
			builder.AppendLine("if (!reader.Options.HasHeader) global::Csv.CsvSerializationException.ThrowHeaderRequired();");

			builder.AppendLine("var allowComments = reader.Options.AllowComments;");
			builder.AppendLine("while (reader.TryReadEndOfLine(true) || (allowComments && reader.TrySkipComment(false))) { }");

			builder.AppendLine($"using var map = new global::Csv.Internal.TempList<int>();");
			builder.AppendLine("var keyBuffer = new global::Csv.Internal.TempList<byte>();");
			using (builder.BeginBlockScope("try"))
			{
				builder.AppendLine("var ___endOfLine = false;");

				using (builder.BeginBlockScope($"while (reader.Remaining > 0)"))
				{
					builder.AppendLine("reader.ReadUtf8(ref keyBuffer);");
					builder.AppendLine("map.Add(GetColumnIndex(keyBuffer.AsSpan()));");
					builder.AppendLine("keyBuffer.Clear(false);");
					using (builder.BeginBlockScope("if (reader.TryReadEndOfLine(true))"))
					{
						builder.AppendLine("___endOfLine = true;");
						builder.AppendLine("break;");
					}
					builder.AppendLine("reader.TryReadSeparator(false);");
				}

				builder.AppendLine("if (!___endOfLine) reader.SkipLine();");
			}
			using (builder.BeginBlockScope("finally"))
			{
				builder.AppendLine("keyBuffer.Dispose();");
			}

			builder.AppendLine();
		}

		using (builder.BeginBlockScope($"public {type.FullTypeName}[] Deserialize(ref global::Csv.CsvReader reader)"))
		{
			EmitReadHeader();

			builder.AppendLine($"using var list = new TempList<{type.FullTypeName}>();");

			using (builder.BeginBlockScope("while (reader.Remaining > 0)"))
			{
				builder.AppendLine("if (reader.TryReadEndOfLine()) continue;");
				builder.AppendLine("if (allowComments && reader.TrySkipComment(false)) continue;");

				EmitDeserializeLocalVariables(type, builder);
				builder.AppendLine("var ___endOfLine = false;");

				using (builder.BeginBlockScope("foreach (var ___i in map.AsSpan())"))
				{
					using (builder.BeginBlockScope("switch (___i)"))
					{
						for (int i = 0; i < members.Count; i++)
						{
							using (builder.BeginIndentScope($"case {i}:"))
							{
								EmitReadMember(members[i], builder);
								builder.AppendLine("break;");
							}
						}
						using (builder.BeginIndentScope("default:"))
						{
							builder.AppendLine("break;");
						}
					}

					using (builder.BeginBlockScope("if (reader.TryReadEndOfLine(true))"))
					{
						builder.AppendLine("___endOfLine = true;");
						builder.AppendLine("goto ADD_ITEM;");
					}

					builder.AppendLine("if (!reader.TryReadSeparator(false)) goto ADD_ITEM;");
				}

				builder.AppendLine();
				builder.AppendLine("ADD_ITEM:");
				builder.Append("list.Add(");
				using (builder.BeginBlockScope("new()"))
				{
					foreach (var member in type.Members)
					{
						builder.AppendLine($"{member.Symbol.Name} = __{member.Symbol.Name},");
					}
				}
				builder.AppendLine(");");

				builder.AppendLine();
				builder.AppendLine("if (!___endOfLine) reader.SkipLine();");
			}

			builder.AppendLine("return list.AsSpan().ToArray();");
		}

		builder.AppendLine();

		using (builder.BeginBlockScope($"public int Deserialize(ref global::Csv.CsvReader reader, global::System.Span<{type.FullTypeName}> destination)"))
		{
			EmitReadHeader();

			builder.AppendLine("var n = 0;");
			using (builder.BeginBlockScope("while (reader.Remaining > 0)"))
			{
				builder.AppendLine("if (reader.TryReadEndOfLine()) continue;");
				builder.AppendLine("if (allowComments && reader.TrySkipComment(false)) continue;");

				EmitDeserializeLocalVariables(type, builder);
				builder.AppendLine("var ___endOfLine = false;");

				using (builder.BeginBlockScope("foreach (var __i in map.AsSpan())"))
				{
					using (builder.BeginBlockScope("switch (__i)"))
					{
						for (int i = 0; i < members.Count; i++)
						{
							using (builder.BeginIndentScope($"case {i}:"))
							{
								EmitReadMember(members[i], builder);
								builder.AppendLine("break;");
							}
						}
						using (builder.BeginIndentScope("default:"))
						{
							builder.AppendLine("break;");
						}
					}

					using (builder.BeginBlockScope("if (reader.TryReadEndOfLine(true))"))
					{
						builder.AppendLine("___endOfLine = true;");
						builder.AppendLine("goto ADD_ITEM;");
					}

					builder.AppendLine("if (!reader.TryReadSeparator(false)) goto ADD_ITEM;");
				}

				builder.AppendLine();
				builder.AppendLine("ADD_ITEM:");
				using (builder.BeginBlockScope("destination[n++] = new()"))
				{
					foreach (var member in type.Members)
					{
						builder.AppendLine($"{member.Symbol.Name} = __{member.Symbol.Name},");
					}
				}
				builder.AppendLine(";");

				builder.AppendLine();
				builder.AppendLine("if (!___endOfLine) reader.SkipLine();");
			}

			builder.AppendLine("return n;");
		}
	}

	static void EmitGetColumnIndexMethod(TypeMetadata type, CodeBuilder builder)
	{
		using var _ = builder.BeginBlockScope("static int GetColumnIndex(global::System.ReadOnlySpan<byte> key)");
		
		using (builder.BeginBlockScope("switch (key.Length)"))
		{
			foreach (var group in type.Members.GroupBy(x => x.Utf8Key.Length))
			{
				using (builder.BeginIndentScope($"case {group.Key}:"))
				{
					foreach (var member in group)
					{
						builder.AppendLine($"if (key.SequenceEqual({member.Symbol.Name}Utf8Key.AsSpan())) return {member.Index};");
					}
					builder.AppendLine("break;");
				}
			}
		}

		builder.AppendLine("return -1;");
	}

	static void EmitDeserializeLocalVariables(TypeMetadata type, CodeBuilder builder)
	{
		foreach (var member in type.Members)
		{
			builder.AppendLine($"var __{member.Symbol.Name} = default({member.FullTypeName});");
		}
	}

	static void EmitReadMember(MemberMetadata member, CodeBuilder builder)
	{
		switch (member.FullTypeName)
		{
			case "System.Boolean":
			case "bool":
				builder.AppendLine($"__{member.Symbol.Name} = reader.ReadBoolean();");
				break;
			case "System.SByte":
			case "sbyte":
				builder.AppendLine($"__{member.Symbol.Name} = reader.ReadSByte();");
				break;
			case "System.Byte":
			case "byte":
				builder.AppendLine($"__{member.Symbol.Name} = reader.ReadByte();");
				break;
			case "System.Int16":
			case "short":
				builder.AppendLine($"__{member.Symbol.Name} = reader.ReadInt16();");
				break;
			case "System.Int32":
			case "int":
				builder.AppendLine($"__{member.Symbol.Name} = reader.ReadInt32();");
				break;
			case "System.Int64":
			case "long":
				builder.AppendLine($"__{member.Symbol.Name} = reader.ReadInt64();");
				break;
			case "System.UInt16":
			case "ushort":
				builder.AppendLine($"__{member.Symbol.Name} = reader.ReadUInt16();");
				break;
			case "System.UInt32":
			case "uint":
				builder.AppendLine($"__{member.Symbol.Name} = reader.ReadUInt32();");
				break;
			case "System.UInt64":
			case "ulong":
				builder.AppendLine($"__{member.Symbol.Name} = reader.ReadUInt64();");
				break;
			case "System.Single":
			case "float":
				builder.AppendLine($"__{member.Symbol.Name} = reader.ReadSingle();");
				break;
			case "System.Double":
			case "double":
				builder.AppendLine($"__{member.Symbol.Name} = reader.ReadDouble();");
				break;
			case "System.Decimal":
			case "decimal":
				builder.AppendLine($"__{member.Symbol.Name} = reader.ReadDecimal();");
				break;
			case "System.String":
			case "string":
				builder.AppendLine($"__{member.Symbol.Name} = reader.ReadString();");
				break;
			case "System.Char":
			case "char":
				builder.AppendLine($"__{member.Symbol.Name} = reader.ReadChar();");
				break;
			default:
				builder.AppendLine($"__{member.Symbol.Name} = reader.Options.FormatterProvider.GetFormatter<{member.FullTypeName}>().Deserialize(ref reader);");
				break;
		}
	}
}