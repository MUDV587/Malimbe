namespace Malimbe.MemberChangeMethod.Fody
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using global::Fody;
    using Malimbe.Shared;
    using Mono.Cecil;
    using Mono.Cecil.Cil;
    using Mono.Cecil.Rocks;
    using Mono.Collections.Generic;

    // ReSharper disable once UnusedMember.Global
    public sealed class ModuleWeaver : BaseModuleWeaver
    {
        private static readonly string _fullBaseAttributeName = typeof(HandlesMemberChangeAttribute).FullName;
        private static readonly string _fullBeforeChangeAttributeName = typeof(CalledBeforeChangeOfAttribute).FullName;

        private MethodReference _isApplicationPlayingGetterReference;
        private MethodReference _isActiveAndEnabledGetterReference;
        private MethodReference _compilerGeneratedAttributeConstructorReference;
        private TypeReference _behaviourReference;
        private bool _isCompilingForEditor;

        public override bool ShouldCleanReference =>
            // InspectorEditor needs this assembly.
            false;

        public override void Execute()
        {
            FindReferences();

            IEnumerable<MethodDefinition> methodDefinitions =
                ModuleDefinition.GetTypes().SelectMany(definition => definition.Methods);
            foreach (MethodDefinition methodDefinition in methodDefinitions)
            {
                foreach (CustomAttribute attribute in FindAttributes(methodDefinition))
                {
                    if (!FindProperty(methodDefinition, attribute, out PropertyDefinition propertyDefinition))
                    {
                        continue;
                    }

                    if (propertyDefinition.SetMethod == null)
                    {
                        WriteError(
                            $"The method '{methodDefinition.FullName}' is annotated to be called by the setter of the"
                            + $" property '{propertyDefinition.FullName}' but the property has no setter.");
                        continue;
                    }

                    ChangePropertySetterCallsToFieldStore(propertyDefinition, methodDefinition);
                    InsertSetMethodCallIntoPropertySetter(propertyDefinition, methodDefinition, attribute);
                }
            }
        }

        public override IEnumerable<string> GetAssembliesForScanning()
        {
            yield return "UnityEngine";
        }

        private void FindReferences()
        {
            MethodReference ImportPropertyGetter(string typeName, string propertyName) =>
                ModuleDefinition.ImportReference(
                    FindTypeDefinition(typeName).Properties.Single(definition => definition.Name == propertyName).GetMethod);

            _isApplicationPlayingGetterReference = ImportPropertyGetter("UnityEngine.Application", "isPlaying");
            _isActiveAndEnabledGetterReference = ImportPropertyGetter("UnityEngine.Behaviour", "isActiveAndEnabled");
            _compilerGeneratedAttributeConstructorReference = ModuleDefinition.ImportReference(
                FindTypeDefinition("System.Runtime.CompilerServices.CompilerGeneratedAttribute")
                    .Methods.First(definition => definition.IsConstructor));
            _behaviourReference = ModuleDefinition.ImportReference(FindTypeDefinition("UnityEngine.Behaviour"));
            _isCompilingForEditor = DefineConstants.Contains("UNITY_EDITOR");
        }

        private static IEnumerable<CustomAttribute> FindAttributes(ICustomAttributeProvider methodDefinition) =>
            methodDefinition.CustomAttributes.Where(
                    attribute => attribute.AttributeType.Resolve().BaseType.FullName == _fullBaseAttributeName)
                .ToList();

        private bool FindProperty(
            MethodDefinition methodDefinition,
            ICustomAttribute attribute,
            out PropertyDefinition propertyDefinition)
        {
            string propertyName = (string)attribute.ConstructorArguments.Single().Value;
            TypeDefinition typeDefinition = methodDefinition.DeclaringType;
            propertyDefinition = null;

            while (typeDefinition != null)
            {
                propertyDefinition =
                    typeDefinition.Properties?.SingleOrDefault(definition => definition.Name == propertyName);
                if (propertyDefinition != null)
                {
                    break;
                }

                typeDefinition = typeDefinition.BaseType.Resolve();
            }

            if (propertyDefinition == null)
            {
                WriteError(
                    $"The method '{methodDefinition.FullName}' is annotated to be called by the setter of the"
                    + $" property '{propertyName}' but the property doesn't exist.");
                return false;
            }

            if (methodDefinition.ReturnType.FullName == TypeSystem.VoidReference.FullName
                && methodDefinition.Parameters?.Count == 0)
            {
                return true;
            }

            WriteError(
                $"The method '{methodDefinition.FullName}' is annotated to be called by the setter of the"
                + $" property '{propertyName}' but the method signature doesn't match. The expected signature is"
                + $" 'void {methodDefinition.Name}()'.");
            propertyDefinition = null;

            return false;
        }

        private void ChangePropertySetterCallsToFieldStore(
            PropertyDefinition propertyDefinition,
            MethodDefinition methodDefinition)
        {
            MethodBody methodBody = methodDefinition.Body;
            Collection<Instruction> instructions = methodBody.Instructions;

            IEnumerable<Instruction> setterCallInstructions = instructions.Where(
                instruction =>
                    (instruction.OpCode == OpCodes.Call
                        || instruction.OpCode == OpCodes.Calli
                        || instruction.OpCode == OpCodes.Callvirt)
                    && instruction.Operand is MethodReference reference
                    && reference.FullName == propertyDefinition.SetMethod.FullName);
            FieldReference backingField = propertyDefinition.FindBackingField();

            foreach (Instruction setterCallInstruction in setterCallInstructions)
            {
                setterCallInstruction.OpCode = OpCodes.Stfld;
                setterCallInstruction.Operand = backingField;

                WriteInfo(
                    $"Changed the property setter call in '{methodDefinition.FullName}' to set the backing"
                    + $" field '{backingField.FullName}' instead to prevent a potential infinite loop.");
            }

            methodBody.OptimizeMacros();
        }

        private void InsertSetMethodCallIntoPropertySetter(
            PropertyDefinition propertyDefinition,
            MethodReference methodReference,
            ICustomAttribute attribute)
        {
            TypeReference methodDeclaringType = methodReference.DeclaringType;

            MethodBody methodBody = propertyDefinition.SetMethod.Body;
            Collection<Instruction> instructions = methodBody.Instructions;

            FieldReference backingField = propertyDefinition.FindBackingField();
            List<Instruction> storeInstructions = instructions.Where(
                    instruction => instruction.OpCode == OpCodes.Stfld
                        && (instruction.Operand as FieldReference)?.FullName == backingField.FullName)
                .ToList();

            foreach (Instruction storeInstruction in storeInstructions)
            {
                Instruction targetInstruction;
                int instructionIndex;
                bool needsPlayingCheck = _isCompilingForEditor;
                bool needsActiveAndEnabledCheck = methodDeclaringType.Resolve().IsSubclassOf(_behaviourReference);

                /*
                 if (Application.isPlaying)                         // Only if compiling for Editor
                 {
                     if (this.isActiveAndEnabled)                   // Only if in a Behaviour
                     {
                         this.Method();
                     }
                 }
                 */

                bool IsPlayingCheck(Instruction instruction) =>
                    instruction.OpCode == OpCodes.Call
                    && (instruction.Operand as MethodReference)?.FullName
                    == _isApplicationPlayingGetterReference.FullName;

                bool IsActiveAndEnabledCheck(Instruction instruction) =>
                    instruction.OpCode == OpCodes.Call
                    && (instruction.Operand as MethodReference)?.FullName
                    == _isActiveAndEnabledGetterReference.FullName;

                if (attribute.AttributeType.FullName == _fullBeforeChangeAttributeName)
                {
                    targetInstruction = storeInstruction.Previous.Previous;
                    instructionIndex = instructions.IndexOf(targetInstruction) - 1;

                    Instruction testInstruction = targetInstruction.Previous;

                    void TryFindExistingCheck(ref bool needsCheck, Func<Instruction, bool> predicate)
                    {
                        if (!needsCheck)
                        {
                            return;
                        }

                        while (testInstruction != null)
                        {
                            if (predicate(testInstruction))
                            {
                                needsCheck = false;
                                return;
                            }

                            testInstruction = testInstruction.Previous;
                        }

                        while (testInstruction != null
                            && (testInstruction.OpCode == OpCodes.Brfalse
                                || testInstruction.OpCode == OpCodes.Brfalse_S))
                        {
                            testInstruction = testInstruction.Next;
                        }

                        if (testInstruction != null)
                        {
                            instructionIndex = instructions.IndexOf(testInstruction) - 1;
                        }
                    }

                    TryFindExistingCheck(ref needsActiveAndEnabledCheck, IsActiveAndEnabledCheck);
                    TryFindExistingCheck(ref needsPlayingCheck, IsPlayingCheck);
                }
                else
                {
                    targetInstruction = storeInstruction.Next;
                    instructionIndex = instructions.IndexOf(targetInstruction) - 1;

                    Instruction testInstruction = storeInstruction.Next;

                    void TryFindExistingCheck(ref bool needsCheck, Func<Instruction, bool> predicate)
                    {
                        if (!needsCheck)
                        {
                            return;
                        }

                        while (testInstruction != null)
                        {
                            if (predicate(testInstruction))
                            {
                                needsCheck = false;
                                instructionIndex = instructions.IndexOf(testInstruction) + 1;
                                return;
                            }

                            testInstruction = testInstruction.Next;
                        }
                    }

                    TryFindExistingCheck(ref needsPlayingCheck, IsPlayingCheck);
                    TryFindExistingCheck(ref needsActiveAndEnabledCheck, IsActiveAndEnabledCheck);
                }

                if (needsPlayingCheck)
                {
                    // Call Application.isPlaying getter
                    instructions.Insert(
                        ++instructionIndex,
                        Instruction.Create(OpCodes.Call, _isApplicationPlayingGetterReference));
                    // Bail out if false
                    instructions.Insert(++instructionIndex, Instruction.Create(OpCodes.Brfalse, targetInstruction));
                }

                if (needsActiveAndEnabledCheck)
                {
                    // Load this (for getter call)
                    instructions.Insert(++instructionIndex, Instruction.Create(OpCodes.Ldarg_0));
                    // Call Behaviour.isActiveAndEnabled getter
                    instructions.Insert(
                        ++instructionIndex,
                        Instruction.Create(OpCodes.Call, _isActiveAndEnabledGetterReference));
                    // Bail out if false
                    instructions.Insert(++instructionIndex, Instruction.Create(OpCodes.Brfalse, targetInstruction));
                }

                TypeDefinition definitionDeclaringType = propertyDefinition.DeclaringType;
                if (methodDeclaringType.FullName != definitionDeclaringType.FullName)
                {
                    MethodDefinition baseMethodDefinition =
                        definitionDeclaringType.Methods.FirstOrDefault(
                            definition => definition.FullName == methodReference.FullName);
                    if (baseMethodDefinition == null)
                    {
                        MethodDefinition methodDefinition = methodReference.Resolve();
                        baseMethodDefinition = new MethodDefinition(
                            methodReference.Name,
                            methodDefinition.Attributes,
                            methodReference.ReturnType);
                        baseMethodDefinition.CustomAttributes.Add(
                            new CustomAttribute(_compilerGeneratedAttributeConstructorReference));
                        definitionDeclaringType.Methods.Add(baseMethodDefinition);

                        // Return
                        baseMethodDefinition.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));

                        if (!baseMethodDefinition.IsFamily)
                        {
                            baseMethodDefinition.IsFamily = true;
                        }

                        if (!baseMethodDefinition.IsVirtual && !baseMethodDefinition.IsNewSlot)
                        {
                            baseMethodDefinition.IsNewSlot = true;
                        }

                        baseMethodDefinition.IsFinal = false;
                        baseMethodDefinition.IsVirtual = true;
                        baseMethodDefinition.IsHideBySig = true;

                        methodDefinition.IsPrivate = baseMethodDefinition.IsPrivate;
                        methodDefinition.IsFamily = baseMethodDefinition.IsFamily;
                        methodDefinition.IsFamilyAndAssembly = baseMethodDefinition.IsFamilyAndAssembly;

                        methodDefinition.IsFinal = false;
                        methodDefinition.IsVirtual = true;
                        methodDefinition.IsNewSlot = false;
                        methodDefinition.IsReuseSlot = true;
                        methodDefinition.IsHideBySig = true;

                        WriteInfo(
                            $"Changed the method '{methodDefinition.FullName}' to override the"
                            + $" newly added base method '{baseMethodDefinition.FullName}'.");
                    }

                    methodReference = baseMethodDefinition;
                }

                // Load this (for method call)
                instructions.Insert(++instructionIndex, Instruction.Create(OpCodes.Ldarg_0));
                // Call method
                instructions.Insert(
                    ++instructionIndex,
                    Instruction.Create(OpCodes.Callvirt, methodReference.CreateGenericMethodIfNeeded()));

                methodBody.OptimizeMacros();

                WriteInfo(
                    $"Inserted a call to the method '{methodReference.FullName}' into"
                    + $" the setter of the property '{propertyDefinition.FullName}'.");
            }
        }
    }
}
