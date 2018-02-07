﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

using Mono.Cecil;
using CSHotFix.Runtime.Intepreter;
using CSHotFix.Runtime.Enviorment;
using CSHotFix.CLR.TypeSystem;
using CSHotFix.Runtime.Stack;
using CSHotFix.CLR.Utils;
namespace CSHotFix.CLR.Method
{
    public class CLRMethod : IMethod
    {
        MethodInfo def;
        ConstructorInfo cDef;
        List<IType> parameters;
        CSHotFix.Runtime.Enviorment.AppDomain appdomain;
        CLRType declaringType;
        ParameterInfo[] param;
        bool isConstructor;
        CLRRedirectionDelegate redirect;
        IType[] genericArguments;
        object[] invocationParam;
        bool isDelegateInvoke;
        int hashCode = -1;
        static int instance_id = 0x20000000;

        public IType DeclearingType
        {
            get
            {
                return declaringType;
            }
        }
        public string Name
        {
            get
            {
                return def.Name;
            }
        }
        public bool HasThis
        {
            get
            {
                return isConstructor ? !cDef.IsStatic : !def.IsStatic;
            }
        }
        public int GenericParameterCount
        {
            get
            {
                if (def.ContainsGenericParameters && def.IsGenericMethodDefinition)
                {
                    return def.GetGenericArguments().Length;
                }
                return 0;
            }
        }
        public bool IsGenericInstance
        {
            get
            {
                return genericArguments != null;
            }
        }

        public bool IsDelegateInvoke
        {
            get
            {
                return isDelegateInvoke;
            }
        }

        public bool IsStatic
        {
            get { return def.IsStatic; }
        }

        public CLRRedirectionDelegate Redirection { get { return redirect; } }

        public MethodInfo MethodInfo { get { return def; } }

        public ConstructorInfo ConstructorInfo { get { return cDef; } }

        public IType[] GenericArguments { get { return genericArguments; } }

        public CLRMethod(MethodInfo def, CLRType type, CSHotFix.Runtime.Enviorment.AppDomain domain)
        {
            this.def = def;
            declaringType = type;
            this.appdomain = domain;
            param = def.GetParameters();
            if (!def.ContainsGenericParameters)
            {
                ReturnType = domain.GetType(def.ReturnType.FullName);
                if (ReturnType == null)
                {
                    ReturnType = domain.GetType(def.ReturnType.AssemblyQualifiedName);
                }
            }
            if (type.IsDelegate && def.Name == "Invoke")
                isDelegateInvoke = true;
            isConstructor = false;

            if (def != null)
            {
                if (def.IsGenericMethod && !def.IsGenericMethodDefinition)
                {
                    appdomain.RedirectMap.TryGetValue(def.GetGenericMethodDefinition(), out redirect);
                }
                else
                    appdomain.RedirectMap.TryGetValue(def, out redirect);
            }
        }
        public CLRMethod(ConstructorInfo def, CLRType type, CSHotFix.Runtime.Enviorment.AppDomain domain)
        {
            this.cDef = def;
            declaringType = type;
            this.appdomain = domain;
            param = def.GetParameters();
            if (!def.ContainsGenericParameters)
            {
                ReturnType = type;
            }
            isConstructor = true;

            if (def != null)
            {
                appdomain.RedirectMap.TryGetValue(cDef, out redirect);
            }
        }

        public int ParameterCount
        {
            get
            {
                return param != null ? param.Length : 0;
            }
        }


        public List<IType> Parameters
        {
            get
            {
                if (parameters == null)
                {
                    InitParameters();
                }
                return parameters;
            }
        }

        public IType ReturnType
        {
            get;
            private set;
        }

        public bool IsConstructor
        {
            get
            {
                return cDef != null;
            }
        }

        void InitParameters()
        {
            parameters = new List<IType>();
            foreach (var i in param)
            {
                IType type = appdomain.GetType(i.ParameterType.FullName);
                if (type == null)
                    type = appdomain.GetType(i.ParameterType.AssemblyQualifiedName);
                if (i.ParameterType.IsGenericTypeDefinition)
                {
                    if (type == null)
                        type = appdomain.GetType(i.ParameterType.GetGenericTypeDefinition().FullName);
                    if (type == null)
                        type = appdomain.GetType(i.ParameterType.GetGenericTypeDefinition().AssemblyQualifiedName);
                }
                if (i.ParameterType.ContainsGenericParameters)
                {
                    var t = i.ParameterType;
                    if (t.HasElementType)
                        t = i.ParameterType.GetElementType();
                    else if (t.GetGenericArguments().Length > 0)
                    {
                        t = t.GetGenericArguments()[0];
                    }
                    type = new ILGenericParameterType(t.Name);
                }
                if (type == null)
                    throw new TypeLoadException();
                parameters.Add(type);
            }
        }

        unsafe StackObject* Minus(StackObject* a, int b)
        {
            return (StackObject*)((long)a - sizeof(StackObject) * b);
        }

        public unsafe object Invoke(Runtime.Intepreter.ILIntepreter intepreter, StackObject* esp, IList<object> mStack, bool isNewObj = false)
        {
            if (parameters == null)
            {
                InitParameters();
            }
            int paramCount = ParameterCount;
            if (invocationParam == null)
                invocationParam = new object[paramCount];
            object[] param = invocationParam;
            for (int i = paramCount; i >= 1; i--)
            {
                var p = Minus(esp, i);
                var obj = this.param[paramCount - i].ParameterType.CheckCLRTypes(StackObject.ToObject(p, appdomain, mStack));
                obj = ILIntepreter.CheckAndCloneValueType(obj, appdomain);
                param[paramCount - i] = obj;
            }

            if (isConstructor)
            {
                if (!isNewObj)
                {
                    if (!cDef.IsStatic)
                    {
                        object instance = declaringType.TypeForCLR.CheckCLRTypes(StackObject.ToObject((Minus(esp, paramCount + 1)), appdomain, mStack));
                        if (instance == null)
                            throw new NullReferenceException();
                        if (instance is CrossBindingAdaptorType && paramCount == 0)//It makes no sense to call the Adaptor's default constructor
                            return null;
                        cDef.Invoke(instance, param);
                        return null;
                    }
                    else
                    {
                        throw new NotImplementedException();
                    }
                }
                else
                {
                    var res = cDef.Invoke(param);

                    FixReference(paramCount, esp, param, mStack);
                    return res;
                }

            }
            else
            {
                object instance = null;

                if (!def.IsStatic)
                {
                    instance = declaringType.TypeForCLR.CheckCLRTypes(StackObject.ToObject((Minus(esp, paramCount + 1)), appdomain, mStack));
                    if (instance == null)
                        throw new NullReferenceException();
                }
                object res = null;
                /*if (redirect != null)
                    res = redirect(new ILContext(appdomain, intepreter, esp, mStack, this), instance, param, genericArguments);
                else*/
                {
                    res = def.Invoke(instance, param);
                }

                FixReference(paramCount, esp, param, mStack);
                return res;
            }
        }

        unsafe void FixReference(int paramCount, StackObject* esp, object[] param, IList<object> mStack)
        {
            for (int i = paramCount; i >= 1; i--)
            {
                var p = Minus(esp, i);
                var val = param[paramCount - i];
                switch (p->ObjectType)
                {
                    case ObjectTypes.StackObjectReference:
                        {
                            var dst = *(StackObject**)&p->Value;
                            if (dst->ObjectType >= ObjectTypes.Object)
                            {
                                var obj = val;
                                if (obj is CrossBindingAdaptorType)
                                    obj = ((CrossBindingAdaptorType)obj).ILInstance;
                                mStack[dst->Value] = obj;
                            }
                            else
                            {
                                ILIntepreter.UnboxObject(dst, val);
                            }
                        }
                        break;
                    case ObjectTypes.FieldReference:
                        {
                            var obj = mStack[p->Value];
                            if(obj is ILTypeInstance)
                            {
                                ((ILTypeInstance)obj)[p->ValueLow] = val;
                            }
                            else
                            {
                                var t = appdomain.GetType(obj.GetType()) as CLRType;
                                t.GetField(p->ValueLow).SetValue(obj, val);
                            }
                        }
                        break;
                    case ObjectTypes.StaticFieldReference:
                        {
                            var t = appdomain.GetType(p->Value);
                            if(t is ILType)
                            {
                                ((ILType)t).StaticInstance[p->ValueLow] = val;
                            }
                            else
                            {
                                ((CLRType)t).SetStaticFieldValue(p->ValueLow, val);
                            }
                        }
                        break;
                }
            }
        }

        public IMethod MakeGenericMethod(IType[] genericArguments)
        {
            Type[] p = new Type[genericArguments.Length];
            for (int i = 0; i < genericArguments.Length; i++)
            {
                p[i] = genericArguments[i].TypeForCLR;
            }
            var t = def.MakeGenericMethod(p);
            var res = new CLRMethod(t, declaringType, appdomain);
            res.genericArguments = genericArguments;
            return res;
        }

        public override string ToString()
        {
            if (def != null)
                return def.ToString();
            else
                return cDef.ToString();
        }

        public override int GetHashCode()
        {
            if (hashCode == -1)
                hashCode = System.Threading.Interlocked.Add(ref instance_id, 1);
            return hashCode;
        }
    }
}