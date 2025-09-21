using System;

namespace Saphirah.AuthoringGenerator
{
    [AttributeUsage(AttributeTargets.Struct, Inherited = false)]
    public sealed class GenerateAuthoringAttribute : Attribute { }
}