// <copyright file="Program.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

// Inspect metadata only. Never load or execute plugin code in the installer.
using var stream = File.OpenRead(args[0]);
using var pe = new PEReader(stream);
var metadata = pe.GetMetadataReader();
var count = 0;
foreach (var handle in metadata.TypeDefinitions)
{
    var type = metadata.GetTypeDefinition(handle);
    if ((type.Attributes & TypeAttributes.Sealed) == 0 || type.BaseType.Kind != HandleKind.TypeSpecification) continue;
    var signature = metadata.GetBlobReader(metadata.GetTypeSpecification((TypeSpecificationHandle)type.BaseType).Signature);
    if (signature.ReadByte() != 0x15 || signature.ReadByte() != 0x12) continue;
    var coded = signature.ReadCompressedInteger();
    string name, space;
    if ((coded & 3) == 1)
    {
        var parent = metadata.GetTypeReference(MetadataTokens.TypeReferenceHandle(coded >> 2));
        name = metadata.GetString(parent.Name); space = metadata.GetString(parent.Namespace);
    }
    else if ((coded & 3) == 0)
    {
        var parent = metadata.GetTypeDefinition(MetadataTokens.TypeDefinitionHandle(coded >> 2));
        name = metadata.GetString(parent.Name); space = metadata.GetString(parent.Namespace);
    }
    else continue;
    if (name == "PCore`1" && space == "GameHelper.Plugin") count++;
}
return count == 1 ? 0 : 1;
