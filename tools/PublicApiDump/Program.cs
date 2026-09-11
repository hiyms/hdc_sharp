// 开发期门禁工具（不随库发布）：以反射打印 HdcSharp 的公共 API 面，一行一成员、稳定排序，
// 供 scripts/check-public-api.ps1 与 scripts/public-api-baseline.txt（spec §5 冻结基线）逐行比对。
// 库本体不使用反射；反射只出现在本工具与单元测试中。

using System.Globalization;
using System.Reflection;
using System.Text;
using HdcSharp;

StringBuilder output = new();
Assembly assembly = typeof(HdcHost).Assembly;

foreach (Type type in assembly.GetExportedTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
{
    string kind = type.IsInterface ? "interface"
        : type.IsEnum ? "enum"
        : type.IsValueType ? "struct"
        : type.IsSealed ? "sealed class"
        : "class";
    output.AppendLine(CultureInfo.InvariantCulture, $"TYPE {kind} {type.FullName}");

    foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                 .OrderBy(f => f.Name, StringComparer.Ordinal))
    {
        string value = field.IsLiteral ? $" = {field.GetRawConstantValue()}" : string.Empty;
        output.AppendLine(CultureInfo.InvariantCulture, $"  FIELD {Format(field.FieldType)} {field.Name}{value}");
    }

    foreach (ConstructorInfo ctor in type.GetConstructors().OrderBy(c => FormatParameters(c.GetParameters()), StringComparer.Ordinal))
    {
        output.AppendLine(CultureInfo.InvariantCulture, $"  CTOR ({FormatParameters(ctor.GetParameters())})");
    }

    foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                 .OrderBy(p => p.Name, StringComparer.Ordinal))
    {
        string accessors = string.Concat(
            property.GetMethod is null ? string.Empty : " get;",
            property.SetMethod is null ? string.Empty : IsInitOnly(property.SetMethod) ? " init;" : " set;");
        output.AppendLine(CultureInfo.InvariantCulture, $"  PROP {Format(property.PropertyType)} {property.Name}{{{accessors} }}");
    }

    foreach (EventInfo evt in type.GetEvents(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                 .OrderBy(e => e.Name, StringComparer.Ordinal))
    {
        output.AppendLine(CultureInfo.InvariantCulture, $"  EVENT {Format(evt.EventHandlerType!)} {evt.Name}");
    }

    foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                 .Where(m => !m.IsSpecialName)
                 .OrderBy(m => m.Name, StringComparer.Ordinal)
                 .ThenBy(m => FormatParameters(m.GetParameters()), StringComparer.Ordinal))
    {
        output.AppendLine(CultureInfo.InvariantCulture, $"  METHOD {Format(method.ReturnType)} {method.Name}({FormatParameters(method.GetParameters())})");
    }
}

Console.Write(output.ToString());
return 0;

static string FormatParameters(ParameterInfo[] parameters)
    => string.Join(", ", parameters.Select(p => $"{Format(p.ParameterType)} {p.Name}{(p.HasDefaultValue ? $" = {p.DefaultValue ?? "null"}" : string.Empty)}"));

static bool IsInitOnly(MethodInfo setter)
    => setter.ReturnParameter.GetRequiredCustomModifiers().Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit");

static string Format(Type type)
{
    Type? nullable = Nullable.GetUnderlyingType(type);
    if (nullable is not null)
    {
        return $"{Format(nullable)}?";
    }

    if (type.IsByRef)
    {
        return $"{Format(type.GetElementType()!)}&";
    }

    if (type.IsArray)
    {
        return $"{Format(type.GetElementType()!)}[]";
    }

    if (!type.IsGenericType)
    {
        return type.Name;
    }

    string name = type.Name[..type.Name.IndexOf('`')];
    return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(Format))}>";
}
