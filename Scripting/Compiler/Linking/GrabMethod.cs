using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Collections.Generic;

namespace IronAHK.Scripting
{
    partial class MethodCollection : List<MethodInfo>
    {
        static OpCode [] one_byte_opcodes;
        static OpCode [] two_bytes_opcodes;
        
        internal const string Prefix = ".builtin_";
        internal const string StatConPrefix = Prefix+"_pseudostatic_";
        
        public List<Type> Sources;
        public TypeBuilder Target;
        public ModuleBuilder Module;
        
        Dictionary<MethodBase, MethodBuilder> MethodsDone;
        Dictionary<ConstructorInfo, ConstructorBuilder> ConstructorsDone;
        Dictionary<FieldInfo, FieldBuilder> FieldsDone;
        Dictionary<Type, TypeBuilder> TypesDone;

        static MethodCollection ()
        {
            // Courtesy of Jb Evain, http://github.com/jbevain/mono.reflection
            one_byte_opcodes = new OpCode [0xe1];
            two_bytes_opcodes = new OpCode [0x1f];

            var fields = typeof (OpCodes).GetFields (
                BindingFlags.Public | BindingFlags.Static);

            for (int i = 0; i < fields.Length; i++) {
                var opcode = (OpCode) fields [i].GetValue (null);
                if (opcode.OpCodeType == OpCodeType.Nternal)
                    continue;

                if (opcode.Size == 1)
                    one_byte_opcodes [opcode.Value] = opcode;
                else
                    two_bytes_opcodes [opcode.Value & 0xff] = opcode;
            }
        }
        
        public MethodCollection()
        {
            Sources = new List<Type>();
            
            MethodsDone = new Dictionary<MethodBase, MethodBuilder>();
            ConstructorsDone = new Dictionary<ConstructorInfo, ConstructorBuilder>();
            FieldsDone = new Dictionary<FieldInfo, FieldBuilder>();
            TypesDone = new Dictionary<Type, TypeBuilder>();
        }
        
        public MethodBuilder GrabMethod(MethodInfo Original)
        {
            return GrabMethod(Original, Target);
        }
        
        MethodBuilder GrabMethod(MethodInfo Original, TypeBuilder On)
        {
            if(Original == null) return null;
            
            if(MethodsDone.ContainsKey(Original)) 
                return MethodsDone[Original];
            
            Type ReturnType;
            if(Sources.Contains(Original.ReturnType.DeclaringType))
                ReturnType = GrabType(Original.ReturnType, false);
            else ReturnType = Original.ReturnType;
            
            MethodBuilder Builder = On.DefineMethod(Prefix+Original.Name, Original.Attributes, 
                ReturnType, ParameterTypes(Original));
            ILGenerator Gen = Builder.GetILGenerator();
            
            MethodsDone.Add(Original, Builder);
            
            CopyMethodBody(Original.GetMethodBody(), Gen, Original.Module);
            
            return Builder; 
        }
        
        ConstructorInfo GrabConstructor(ConstructorInfo Original, TypeBuilder On)
        {
            if(ConstructorsDone.ContainsKey(Original))
                return ConstructorsDone[Original];
            
            ConstructorBuilder Builder = On.DefineConstructor(Original.Attributes, 
                Original.CallingConvention, ParameterTypes(Original));
            ILGenerator Gen = Builder.GetILGenerator();
            
            ConstructorsDone.Add(Original, Builder);
            
            CopyMethodBody(Original.GetMethodBody(), Gen, Original.Module);
            
            return Builder;
        }
        
        void CopyMethodBody(MethodBody Body, ILGenerator Gen, Module Origin)
        {
            CopyLocals(Gen, Body);
            
            var ExceptionTrinkets = new List<int>();
            MineExTrinkets(Body, ExceptionTrinkets);
            
            byte[] Bytes = Body.GetILAsByteArray();
            
            for(int i = 0; i < Bytes.Length; i++)
            {
                CopyTryCatch(Gen, i, Body, ExceptionTrinkets);
                CopyOpcode(Bytes, ref i, Gen, Origin);
            }
        }
        
        // Build a cache of points where we need to look for exception trinkets
        // An exception trinket is an object denoting the offsets of try, catch and finally blocks
        void MineExTrinkets(MethodBody Body, List<int> ExceptionTrinkets)
        {
            foreach(ExceptionHandlingClause Clause in Body.ExceptionHandlingClauses)
            {
                // Only handle catch and finally. TODO: fault and filter
                if(Clause.Flags != ExceptionHandlingClauseOptions.Clause &&
                   Clause.Flags != ExceptionHandlingClauseOptions.Finally) 
                    continue;
                
                ExceptionTrinkets.Add(Clause.TryOffset);
                ExceptionTrinkets.Add(Clause.HandlerOffset);
                ExceptionTrinkets.Add(Clause.HandlerOffset+Clause.HandlerLength);
            }
        }
        
        void CopyTryCatch(ILGenerator Gen, int i, MethodBody Body, List<int> ExceptionTrinkets)
        {
            // Quick check to see if we want to walk through the list
            if(!ExceptionTrinkets.Contains(i)) return;
            
            foreach(ExceptionHandlingClause Clause in Body.ExceptionHandlingClauses)
            {
                if(Clause.Flags != ExceptionHandlingClauseOptions.Clause &&
                   Clause.Flags != ExceptionHandlingClauseOptions.Finally) 
                    continue;
                
                // Look for an ending of an exception block first!
                if(Clause.HandlerOffset+Clause.HandlerLength == i)
                    Gen.EndExceptionBlock();
                
                // If this marks the beginning of a try block, emit that
                if(Clause.TryOffset == i)
                    Gen.BeginExceptionBlock();
                
                // Also check for the beginning of a catch block
                if(Clause.HandlerOffset == i && Clause.Flags == ExceptionHandlingClauseOptions.Clause)
                    Gen.BeginCatchBlock(Clause.CatchType);
                
                // Lastly, check for a finally block
                if(Clause.HandlerOffset == i && Clause.Flags == ExceptionHandlingClauseOptions.Finally)
                    Gen.BeginFinallyBlock();
            }
        }
        
        // Initialise the variables. TODO: Obey InitLocals
        void CopyLocals(ILGenerator Gen, MethodBody Body)
        {
            foreach(LocalVariableInfo Info in Body.LocalVariables) 
                Gen.DeclareLocal(Info.LocalType, Info.IsPinned);
        }
        
        void CopyOpcode(byte[] Bytes, ref int i, ILGenerator Gen, Module Origin)
        {
            OpCode Code;
            if(Bytes[i] == 0xFE) Code = two_bytes_opcodes[Bytes[++i]];
            else Code = one_byte_opcodes[Bytes[i]];
            
            // These are emitted by exception handling copier
            if(Code == OpCodes.Leave) 
            {
                i += 4;
                return; 
            }
            else if(Code == OpCodes.Endfinally) return; 
            
            switch(Code.OperandType)
            {
                // If no argument, then re-emit the opcode
                case OperandType.InlineNone:
                {
                    Gen.Emit(Code);
                    break;
                }
                    
                // If argument is a method, re-emit the method reference
                case OperandType.InlineMethod:
                {
                    int Token = BitHelper.ReadInteger(Bytes, ref i);
                    MethodBase Base = Origin.ResolveMethod(Token);
                    
                    if(Base is MethodInfo)
                    {
                        MethodInfo Info = Base as MethodInfo;
                        
                        // If the method we're copying calls another method from 
                        // the list of sources, we need to copy that as well
                        if(Sources.Contains(Info.DeclaringType))
                            Info = GrabMethod(Info);
                        
                        Gen.Emit(Code, Info);
                    }
                    else if(Base is ConstructorInfo)
                        Gen.Emit(Code, Base as ConstructorInfo);
                    else throw new InvalidOperationException("Inline method is neither method nor constructor.");
                    
                    break;
                }
                    
                // Argument is a field reference
                case OperandType.InlineField:
                {
                    int Token = BitHelper.ReadInteger(Bytes, ref i);
                    FieldInfo Field = Origin.ResolveField(Token);
                    
                    if(Sources.Contains(Field.DeclaringType))
                        Field = GrabField(Field);
                    
                    Gen.Emit(Code, Field);
                    break;
                }
                    
                // Argument is a type reference
                case OperandType.InlineType:
                {
                    int Token = BitHelper.ReadInteger(Bytes, ref i);
                    Type Ref = Origin.ResolveType(Token);
                    Gen.Emit(Code, Ref);
                    break;
                }
                    
                // Argument is an inline string
                case OperandType.InlineString:
                {
                    int Token = BitHelper.ReadInteger(Bytes, ref i);
                    string Copy = Origin.ResolveString(Token);
                    Gen.Emit(Code, Copy);
                    break;
                }
                 
                // Argument is a metadata token
                case OperandType.InlineTok:
                {
                    int Token = BitHelper.ReadInteger(Bytes, ref i);
                    MemberInfo Info = Origin.ResolveMember(Token);
                    
                    if(Info.MemberType == MemberTypes.Field)
                    {
                        if(Sources.Contains(Info.DeclaringType))
                            Gen.Emit(OpCodes.Ldtoken, GrabField(Info as FieldInfo));
                        else Gen.Emit(OpCodes.Ldtoken, Info as FieldInfo);
                    }
                    else if(Info.MemberType == MemberTypes.Method)
                    {
                        if(Sources.Contains(Info.DeclaringType))
                            Gen.Emit(OpCodes.Ldtoken, GrabMethod(Info as MethodInfo));
                        else Gen.Emit(OpCodes.Ldtoken, Info as MethodInfo);
                    }
                    else if(Info.MemberType == MemberTypes.TypeInfo)
                    {
                        if(Sources.Contains(Info.DeclaringType))
                            Gen.Emit(OpCodes.Ldtoken, GrabType(Info as Type, true));
                        else if(Sources.Contains(Info as Type))
                            Gen.Emit(OpCodes.Ldtoken, Target);
                        else Gen.Emit(OpCodes.Ldtoken, Info as Type);
                    }
                    else throw new InvalidOperationException("Inline token is neither field, nor method, nor type");
                    
                    break;
                }
                    
                // Argument is a byte
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                {
                    Gen.Emit(Code, Bytes[++i]);
                    break;
                }
                
                // Argument is a short
                case OperandType.InlineVar:
                {
                    Gen.Emit(Code, BitHelper.ReadShort(Bytes, ref i));
                    break;
                }
                    
                // Argument is a 32-bit integer
                case OperandType.InlineSwitch:
                case OperandType.InlineBrTarget:
                case OperandType.InlineI:
                case OperandType.ShortInlineR: // This is actually a float, but we don't care
                {
                    Gen.Emit(Code, BitHelper.ReadInteger(Bytes, ref i));
                    break;
                }
                    
                // Argument is a 64-bit integer
                case OperandType.InlineI8:
                {
                    Gen.Emit(Code, BitHelper.ReadLong(Bytes, ref i));
                    break;
                }
                    
                // Argument is a 32-bit float
                case OperandType.InlineR:
                {
                    Gen.Emit(Code, BitHelper.ReadDouble(Bytes, ref i));
                    break;
                }     
                
                // If ever we run across OpCodes.Calli this'll probably happen
                default:
                    throw new InvalidOperationException("The method copier ran across an unknown opcode.");
            }
        }
        
        public FieldInfo GrabField(FieldInfo Field)
        {
            return GrabField(Field, Target);
        }
        
        FieldInfo GrabField(FieldInfo Field, TypeBuilder On)
        {
            if(FieldsDone.ContainsKey(Field))
               return FieldsDone[Field];
            
            Type FieldType;
            if(Sources.Contains(Field.FieldType))
                FieldType = GrabType(Field.FieldType, true);
            else FieldType = Field.FieldType;
            
            FieldBuilder CopiedField = On.DefineField(Prefix+Field.Name, FieldType, Field.Attributes); 
            FieldsDone.Add(Field, CopiedField);
            
            return CopiedField;
        }
        
        public Type GrabType(Type Copy, bool CopyParent)
        {
            if(TypesDone.ContainsKey(Copy))
                return TypesDone[Copy];
            
            TypeBuilder Ret = Target.DefineNestedType(Prefix+Copy.Name, Copy.Attributes, 
                CopyParent ? Copy.BaseType : null, Copy.GetInterfaces());
            TypesDone.Add(Copy, Ret);
            
            // Walk through methods and fields, and copy them
            foreach(MemberInfo Member in Copy.GetMembers())
            {
                if(Member.DeclaringType != Copy) continue;
                
                switch(Member.MemberType)
                {
                    case MemberTypes.Method:
                        GrabMethod(Member as MethodInfo, Ret);
                        break;
                        
                    case MemberTypes.Field:
                        GrabField(Member as FieldInfo, Ret);
                        break;
                        
                    case MemberTypes.Constructor:
                        Console.WriteLine("Grabbing constructor");
                        GrabConstructor(Member as ConstructorInfo, Ret);
                        break;
                }
            }
            
            Ret.CreateType();
            
            return Ret;
        }
        
        static Type[] ParameterTypes(MethodBase Original)
        {
            ParameterInfo[] Params = Original.GetParameters();
            Type[] Ret = new Type[Params.Length];
            
            for(int i = 0; i < Params.Length; i++)
                Ret[i] = Params[i].ParameterType;
            
            return Ret;
        }
    }
}

