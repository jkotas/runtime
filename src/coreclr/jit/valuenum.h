// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Defines the class "ValueNumStore", which maintains value numbers for a compilation.

// Recall that "value numbering" assigns an integer value number to each expression.  The "value
// number property" is that two expressions with the same value number will evaluate to the same value
// at runtime.  Expressions with different value numbers may or may not be equivalent.  This property
// of value numbers has obvious applications in redundancy-elimination optimizations.
//
// Since value numbers give us a way of talking about the (immutable) values to which expressions
// evaluate, they provide a good "handle" to use for attributing properties to values.  For example,
// we might note that some value number represents some particular integer constant -- which has obvious
// application to constant propagation.  Or that we know the exact type of some object reference,
// which might be used in devirtualization.
//
// Finally, we will also use value numbers to express control-flow-dependent assertions.  Some test may
// imply that after the test, something new is known about a value: that an object reference is non-null
// after a dereference (since control flow continued because no exception was thrown); that an integer
// value is restricted to some subrange in after a comparison test; etc.

// In addition to classical numbering, this implementation also performs disambiguation of heap writes,
// using memory SSA and the following aliasing model:
//
// 1. Arrays of different types do not alias - taking into account the array compatibility rules, i. e.
//    "int[] <-> uint[]" and such being allowed.
// 2. Different static fields do not alias (meaning mutable overlapping RVA statics are not supported).
// 3. Different class fields do not alias. Struct fields are allowed to alias - this supports code that
//    does reinterpretation of structs (e. g. "Unsafe.As<StructOne, StructTwo>(...)"), but makes it UB
//    to alias reference types in the same manner (including via explicit layout).
//
// The no aliasing rule for fields should be interpreted to mean that "ld[s]fld[a] FieldOne" cannot refer
// to the same location as "ld[s]fld[a] FieldTwo". The aliasing model above reflects the fact type safety
// rules in .NET largely only apply to reference types, while struct locations can be and often are treated
// by user code (and, importantly, the compiler itself) as simple blobs of bytes.
//
// Abstractly, numbering maintains states of memory in "maps", which are indexed into with various "selectors",
// loads reading from said maps and stores recording new states for them (note that as with everything VN,
// the "maps" are immutable, thus an update is performed via deriving a new map from an existing one).
//
// Due to the fact we allow struct field access to alias, but still want to optimize it, our model has two
// types of maps and selectors: precise and physical. Precise maps allow arbitrary selectors, and if those
// are known to be distinct values (e. g. different constants), the values they select are also presumed to
// represent distinct locations. Physical maps, on the other hand, can only have one type of selector: "the
// physical selector", representing offset of the location and its size (in bytes), where both must be known
// at compile time. Naturally, different physical selectors can refer to overlapping locations.
//
// The following "VNFunc"s are relevant when it comes to map numbering:
//
// 1. "MapSelect" - represents a "value" taken from another map at a given index: "map[index] => value". It is
//    the "VNForMapSelect[Work]" method that represents the core of the selection infrastructure: it performs
//    various reductions based on the maps (listed below) being selected from, before "giving up" and creating
//    a new "MapSelect" VN. "MapSelect"s are used for both precise and physical maps.
// 2. "Phi[Memory]Def" - the PHI function applied to multiple reaching definitions for a given block. PHIs can
//    be reduced by the selection process: "Phi(d:1, d:2, ...)[index]" is evaluated as "Phi(d:1[index], ...)",
//    so if all the inner selections ("d:n[index]") agree, that value is returned as the selected one.
// 3. "MapStore" - this is the precise "update" map, it represents a map after a "set" operation at some index.
//    MapStore VNs naturally "chain" together, the next map representing an update of the previous, and will be
//    traversed by the selection process as long as the store indices are constant, and different from the one
//    being selected (meaning they represent distinct locations): "map[F0 := V0][F1 := V1][F0]" => "V0".
// 4. "MapPhysicalStore" - the physical equivalent to "MapStore", can only be indexed with physical selectors,
//    with the selection rules taking into account aliasability of physical locations.
// 5. "BitCast" - the physical map representing "identity" selection ("map[0:sizeof(map) - 1]"). Exists because
//    physical maps themselves do not have a strong type identity (the physical selector only cares about size).
//    but the VN/IR at large do. Is a no-op in the selection process. One can notice that we could have chosen
//    to represent this concept with an identity "MapPhysicalStore", however, a different "VNFunc" was ultimately
//    chosen due to it being easier to reason about and a little cheaper, with the expectation that "BitCast"s
//    would be reasonably common - the scenario they are meant to handle are stores/loads to/from structs with
//    one field, where the location can be referenced from the IR as both TYP_STRUCT and the field's type.
//
// We give "placeholder" types (TYP_UNDEF and TYP_UNKNOWN as TYP_MEM and TYP_HEAP) to maps that do not represent
// values found in IR, which are currently all precise (though that is not a requirement of the model).
//
// We choose to maintain the following invariants with regards to types of physical locations:
//
// 1. Tree VNs are always "normalized on load" - their types are made to match (via bitcasts). We presume this
//    makes the rest of the compiler code simpler, as it will not have to reason about "TYP_INT" trees having
//    "TYP_FLOAT" value numbers. This normalization is currently not always done; that should be fixed.
// 2. Types of locals are "normalized on store" - this is different from the rest of physical locations, as not
//    only VN looks at these value numbers (stored in SSA descriptors), and similar to the tree case, we presume
//    it is simpler to reason about matching types.
// 3. Types of all other locations (array elements and fields) are not normalized - these only appear in the VN
//    itself as physical maps / values.
//
// Note as well how we handle type identity for structs: we canonicalize on their size. This has the significant
// consequence that any two equally-sized structs can be given the same value number, even if they have different
// ABI characteristics or GC layout. The primary motivations for this are throughout and simplicity, however, we
// would also like the compiler at large to treat structs with compatible layouts as equivalent, so that we can
// propagate copies between them freely.
//
//
// Let's review the following snippet to demonstrate how the MapSelect/MapStore machinery works. Say we have this
// snippet of (C#) code:
//
// int Procedure(OneClass obj, AnotherClass subj, int objVal, int subjVal)
// {
//     obj.StructField.ScalarField = objVal;
//     subj.OtherScalarField = subjVal;
//
//     return obj.StructField.ScalarField + subj.OtherScalarField;
// }
//
// On entry, we assign some VN to the GcHeap (VN mostly only cares about GcHeap, so from now on the term "heap"
// will be used to mean GcHeap), $Heap.
//
// A store to the ScalarField is seen. Now, the value numbering of fields is done in the following pattern for
// maps that it builds: [$Heap][$FirstField][$Object][offset:offset + size of the store]. It may seem odd that
// the indexing is done first for the field, and only then for the object, but the reason for that is the fact
// that it enables MapStores to $Heap to refer to distinct selectors, thus enabling the traversal through the
// map updates when looking for the values that were stored. Were $Object VNs used for this, the traversal could
// not be performed, as two numerically different VNs can, obviously, refer to the same object.
//
// With that in mind, the following maps are first built for the store ("field VNs" - VNs for handles):
//
//  $StructFieldMap       = MapSelect($Heap, $StructField)
//  $StructFieldForObjMap = MapSelect($StructFieldMap, $Obj)
//
// Now that we know where to store, the store maps are built:
//
//  $ScalarFieldSelector     = PhysicalSelector(offsetof(ScalarField), sizeof(ScalarField))
//  $NewStructFieldForObjMap = MapPhysicalStore($StructFieldForObjMap, $ScalarFieldSelector, $ObjVal)
//  $NewStructFieldMap       = MapStore($StructFieldMap, $Obj, $NewStructFieldForObjMap)
//  $NewHeap                 = MapStore($Heap, $StructField, $NewStructFieldMap)
//
// Notice that the maps are built in the opposite order, as we must first know the value of the "narrower" map to
// store into the "wider" map.
//
// Similarly, the numbering is performed for "subj.OtherScalarField = subjVal", and the heap state updated (say to
// $NewHeapWithSubj). Now when we call "VNForMapSelect" to find out the stored values when numbering the reads, the
// following traversal is performed:
//
//   $obj.StructField.AnotherStructField.ScalarField
//     = $NewHeapWithSubj[$StructField][$Obj][$ScalarFieldSelector]:
//         "$NewHeapWithSubj.Index == $StructField" => false (not the needed map).
//         "IsConst($NewHeapWithSubj.Index) && IsConst($StructField)" => true (can continue, non-aliasing indices).
//         "$NewHeap.Index == $StructField" => true, Value is $NewStructFieldMap.
//           "$NewStructFieldMap.Index == $Obj" => true, Value is $NewStructFieldForObjMap.
//             "$NewStructFieldForObjMap.Index == $ScalarFieldSelector" => true, Value is $ObjVal (found it!).
//
// And similarly for the $SubjVal - we end up with a nice $Add($ObjVal, $SubjVal) feeding the return.
//
// While the above example focuses on fields, the idea is universal to all supported location types. Statics are
// modeled as straight indices into the heap (MapSelect($Heap, $Field) returns the value of the field for them),
// arrays - like fields, but with the primiary selector being not the first field, but the "equivalence class" of
// an array, i. e. the type of its elements, taking into account things like "int[]" being legally aliasable as
// "uint[]". Physical maps are used to number local fields.

/*****************************************************************************/
#ifndef _VALUENUM_H_
#define _VALUENUM_H_
/*****************************************************************************/

#include "vartype.h"
// For "GT_COUNT"
#include "gentree.h"
// Defines the type ValueNum.
#include "valuenumtype.h"
// Defines the type SmallHashTable.
#include "compiler.h"
#include "smallhash.h"

// A "ValueNumStore" represents the "universe" of value numbers used in a single
// compilation.

// All members of the enumeration genTreeOps are also members of VNFunc.
// (Though some of these may be labeled "illegal").
enum VNFunc
{
#define GTNODE(en, st, cm, ivn, ok) VNF_##en,
#include "gtlist.h"
    VNF_Boundary = GT_COUNT,
#define ValueNumFuncDef(nm, arity, commute, knownNonNull, sharedStatic) VNF_##nm,
#include "valuenumfuncs.h"
    VNF_COUNT
};

// Given a GenTree node return the VNFunc that should be used when value numbering
//
VNFunc GetVNFuncForNode(GenTree* node);

// An instance of this struct represents an application of the function symbol
// "m_func" to the first "m_arity" (<= 4) argument values in "m_args."
struct VNFuncApp
{
    VNFunc    m_func;
    unsigned  m_arity;
    ValueNum* m_args;

    bool Equals(const VNFuncApp& funcApp)
    {
        if (m_func != funcApp.m_func)
        {
            return false;
        }
        if (m_arity != funcApp.m_arity)
        {
            return false;
        }
        for (unsigned i = 0; i < m_arity; i++)
        {
            if (m_args[i] != funcApp.m_args[i])
            {
                return false;
            }
        }
        return true;
    }
};

struct VNPhiDef
{
    unsigned  LclNum;
    unsigned  SsaDef;
    unsigned* SsaArgs;
    unsigned  NumArgs;
};

struct VNMemoryPhiDef
{
    BasicBlock* Block;
    unsigned*   SsaArgs;
    unsigned    NumArgs;
};

// We use a unique prefix character when printing value numbers in dumps:  i.e.  $1c0
// This define is used with string concatenation to put this in printf format strings
#define FMT_VN "$%x"

// We will use this placeholder type for memory maps that do not represent IR values ("field maps", etc).
static const var_types TYP_MEM = TYP_UNDEF;

// We will use this placeholder type for memory maps representing "the heap" (GcHeap/ByrefExposed).
static const var_types TYP_HEAP = TYP_UNKNOWN;

class ValueNumStore
{

public:
    // We will reserve "max unsigned" to represent "not a value number", for maps that might start uninitialized.
    static const ValueNum NoVN = UINT32_MAX;
    // A second special value, used to indicate that a function evaluation would cause infinite recursion.
    static const ValueNum RecursiveVN = UINT32_MAX - 1;

    // Special value used to represent something that isn't in a loop for VN functions that take loop parameters.
    static const unsigned NoLoop = UINT32_MAX;
    // Special value used to represent something that may or may not be in a loop, so needs to be handled
    // conservatively.
    static const unsigned UnknownLoop = UINT32_MAX - 1;

    // ==================================================================================================
    // VNMap - map from something to ValueNum, where something is typically a constant value or a VNFunc
    //         This class has two purposes - to abstract the implementation and to validate the ValueNums
    //         being stored or retrieved.
    template <class fromType, class keyfuncs = JitLargePrimitiveKeyFuncs<fromType>>
    class VNMap : public JitHashTable<fromType, keyfuncs, ValueNum>
    {
    public:
        VNMap(CompAllocator alloc)
            : JitHashTable<fromType, keyfuncs, ValueNum>(alloc)
        {
        }

        bool Set(fromType k, ValueNum val)
        {
            assert(val != RecursiveVN);
            return JitHashTable<fromType, keyfuncs, ValueNum>::Set(k, val);
        }
        bool Lookup(fromType k, ValueNum* pVal = nullptr) const
        {
            bool result = JitHashTable<fromType, keyfuncs, ValueNum>::Lookup(k, pVal);
            assert(!result || *pVal != RecursiveVN);
            return result;
        }
    };

private:
    Compiler* m_pComp;

    // For allocations.  (Other things?)
    CompAllocator m_alloc;

    // TODO-Cleanup: should transform "attribs" into a struct with bit fields.  That would be simpler...

    enum VNFOpAttrib
    {
        VNFOA_IllegalGenTreeOp = 0x1,  // corresponds to a genTreeOps value that is not a legal VN func.
        VNFOA_Commutative      = 0x2,  // 1 iff the function is commutative.
        VNFOA_Arity1           = 0x4,  // Bits 2,3,4 encode the arity.
        VNFOA_Arity2           = 0x8,  // Bits 2,3,4 encode the arity.
        VNFOA_Arity4           = 0x10, // Bits 2,3,4 encode the arity.
        VNFOA_KnownNonNull     = 0x20, // 1 iff the result is known to be non-null.
        VNFOA_SharedStatic     = 0x40, // 1 iff this VNF is represent one of the shared static jit helpers
    };

    static const unsigned VNFOA_IllegalGenTreeOpShift = 0;
    static const unsigned VNFOA_CommutativeShift      = 1;
    static const unsigned VNFOA_ArityShift            = 2;
    static const unsigned VNFOA_ArityBits             = 3;
    static const unsigned VNFOA_MaxArity              = (1 << VNFOA_ArityBits) - 1; // Max arity we can represent.
    static const unsigned VNFOA_ArityMask             = (VNFOA_Arity4 | VNFOA_Arity2 | VNFOA_Arity1);
    static const unsigned VNFOA_KnownNonNullShift     = 5;
    static const unsigned VNFOA_SharedStaticShift     = 6;

    static_assert_no_msg(unsigned(VNFOA_IllegalGenTreeOp) == (1 << VNFOA_IllegalGenTreeOpShift));
    static_assert_no_msg(unsigned(VNFOA_Commutative) == (1 << VNFOA_CommutativeShift));
    static_assert_no_msg(unsigned(VNFOA_Arity1) == (1 << VNFOA_ArityShift));
    static_assert_no_msg(VNFOA_ArityMask == (VNFOA_MaxArity << VNFOA_ArityShift));
    static_assert_no_msg(unsigned(VNFOA_KnownNonNull) == (1 << VNFOA_KnownNonNullShift));
    static_assert_no_msg(unsigned(VNFOA_SharedStatic) == (1 << VNFOA_SharedStaticShift));

    // These enum constants are used to encode the cast operation in the lowest bits by VNForCastOper
    enum VNFCastAttrib
    {
        VCA_UnsignedSrc = 0x01,

        VCA_BitCount     = 1,    // the number of reserved bits
        VCA_ReservedBits = 0x01, // i.e. (VCA_UnsignedSrc)
    };

    // Helpers and an array of length GT_COUNT, mapping genTreeOp values to their VNFOpAttrib.
    static constexpr uint8_t GetOpAttribsForArity(genTreeOps oper, GenTreeOperKind kind);
    static constexpr uint8_t GetOpAttribsForGenTree(genTreeOps      oper,
                                                    bool            commute,
                                                    bool            illegalAsVNFunc,
                                                    GenTreeOperKind kind);
    static constexpr uint8_t GetOpAttribsForFunc(int arity, bool commute, bool knownNonNull, bool sharedStatic);
    static const uint8_t     s_vnfOpAttribs[];

    // Returns "true" iff gtOper is a legal value number function.
    // (Requires InitValueNumStoreStatics to have been run.)
    static bool GenTreeOpIsLegalVNFunc(genTreeOps gtOper);

    // Returns "true" iff "vnf" is a commutative (and thus binary) operator.
    // (Requires InitValueNumStoreStatics to have been run.)
    static bool VNFuncIsCommutative(VNFunc vnf);

    bool VNEvalCanFoldBinaryFunc(var_types type, VNFunc func, ValueNum arg0VN, ValueNum arg1VN);

    bool VNEvalCanFoldUnaryFunc(var_types type, VNFunc func, ValueNum arg0VN);

    // Returns "true" iff "vnf" should be folded by evaluating the func with constant arguments.
    bool VNEvalShouldFold(var_types typ, VNFunc func, ValueNum arg0VN, ValueNum arg1VN);

    // Value number a type comparison
    ValueNum VNEvalFoldTypeCompare(var_types type, VNFunc func, ValueNum arg0VN, ValueNum arg1VN);

    // return vnf(v0)
    template <typename T>
    static T EvalOp(VNFunc vnf, T v0);

    // returns vnf(v0, v1).
    template <typename T>
    T EvalOp(VNFunc vnf, T v0, T v1);

    // return vnf(v0) or vnf(v0, v1), respectively (must, of course be unary/binary ops, respectively.)
    template <typename T>
    static T EvalOpSpecialized(VNFunc vnf, T v0);
    template <typename T>
    T EvalOpSpecialized(VNFunc vnf, T v0, T v1);

    template <typename T>
    static int EvalComparison(VNFunc vnf, T v0, T v1);

    // Should only instantiate (in a non-trivial way) for "int" and "INT64".  Returns true iff dividing "v0" by "v1"
    // would produce integer overflow (an ArithmeticException -- *not* division by zero, which is separate.)
    template <typename T>
    static bool IsOverflowIntDiv(T v0, T v1);

    // Should only instantiate (in a non-trivial way) for integral types (signed/unsigned int32/int64).
    // Returns true iff v is the zero of the appropriate type.
    template <typename T>
    static bool IsIntZero(T v);

public:
    // Given an constant value number return its value.
    int    GetConstantInt32(ValueNum argVN);
    INT64  GetConstantInt64(ValueNum argVN);
    double GetConstantDouble(ValueNum argVN);
    float  GetConstantSingle(ValueNum argVN);

#if defined(FEATURE_SIMD)
    simd8_t  GetConstantSimd8(ValueNum argVN);
    simd12_t GetConstantSimd12(ValueNum argVN);
    simd16_t GetConstantSimd16(ValueNum argVN);
#if defined(TARGET_XARCH)
    simd32_t GetConstantSimd32(ValueNum argVN);
    simd64_t GetConstantSimd64(ValueNum argVN);
#endif // TARGET_XARCH
#if defined(FEATURE_MASKED_HW_INTRINSICS)
    simdmask_t GetConstantSimdMask(ValueNum argVN);
#endif // FEATURE_MASKED_HW_INTRINSICS
#endif // FEATURE_SIMD

private:
    // Assumes that all the ValueNum arguments of each of these functions have been shown to represent constants.
    // Assumes that "vnf" is a operator of the appropriate arity (unary for the first, binary for the second).
    // Assume that "CanEvalForConstantArgs(vnf)" is true.
    // Returns the result of evaluating the function with those constant arguments.
    ValueNum EvalFuncForConstantArgs(var_types typ, VNFunc vnf, ValueNum vn0);
    ValueNum EvalFuncForConstantArgs(var_types typ, VNFunc vnf, ValueNum vn0, ValueNum vn1);
    ValueNum EvalFuncForConstantFPArgs(var_types typ, VNFunc vnf, ValueNum vn0, ValueNum vn1);
    ValueNum EvalCastForConstantArgs(var_types typ, VNFunc vnf, ValueNum vn0, ValueNum vn1);
    ValueNum EvalBitCastForConstantArgs(var_types dstType, ValueNum arg0VN);

    ValueNum EvalUsingMathIdentity(var_types typ, VNFunc vnf, ValueNum vn0, ValueNum vn1);

// This is the constant value used for the default value of m_mapSelectBudget
#define DEFAULT_MAP_SELECT_BUDGET 100 // used by JitVNMapSelBudget

    // This is the maximum number of MapSelect terms that can be "considered" as part of evaluation of a top-level
    // MapSelect application.
    int m_mapSelectBudget;

    template <typename T, typename NumMap>
    inline ValueNum VnForConst(T cnsVal, NumMap* numMap, var_types varType);

    // returns true iff vn is known to be a constant int32 that is > 0
    bool IsVNPositiveInt32Constant(ValueNum vn);

public:
    // Validate that the new initializer for s_vnfOpAttribs matches the old code.
    static void ValidateValueNumStoreStatics();

    // Initialize an empty ValueNumStore.
    ValueNumStore(Compiler* comp, CompAllocator allocator);

    // Returns "true" iff "vnf" (which may have been created by a cast from an integral value) represents
    // a legal value number function.
    // (Requires InitValueNumStoreStatics to have been run.)
    static bool VNFuncIsLegal(VNFunc vnf)
    {
        return unsigned(vnf) > VNF_Boundary || GenTreeOpIsLegalVNFunc(static_cast<genTreeOps>(vnf));
    }

    // Returns "true" iff "vnf" is one of:
    // VNF_ADD_OVF, VNF_SUB_OVF, VNF_MUL_OVF,
    // VNF_ADD_UN_OVF, VNF_SUB_UN_OVF, VNF_MUL_UN_OVF.
    static bool VNFuncIsOverflowArithmetic(VNFunc vnf);

    // Returns "true" iff "vnf" is VNF_Cast or VNF_CastOvf.
    static bool VNFuncIsNumericCast(VNFunc vnf);

    // Returns the arity of "vnf".
    static unsigned VNFuncArity(VNFunc vnf);

    // Requires "gtOper" to be a genTreeOps legally representing a VNFunc, and returns that
    // VNFunc.
    // (Requires InitValueNumStoreStatics to have been run.)
    static VNFunc GenTreeOpToVNFunc(genTreeOps gtOper)
    {
        assert(GenTreeOpIsLegalVNFunc(gtOper));
        return static_cast<VNFunc>(gtOper);
    }

#ifdef DEBUG
    static void RunTests(Compiler* comp);
#endif // DEBUG

    // This block of methods gets value numbers for constants of primitive types.
    ValueNum VNForIntCon(INT32 cnsVal);
    ValueNum VNForIntPtrCon(ssize_t cnsVal);
    ValueNum VNForLongCon(INT64 cnsVal);
    ValueNum VNForFloatCon(float cnsVal);
    ValueNum VNForDoubleCon(double cnsVal);
    ValueNum VNForByrefCon(target_size_t byrefVal);

#if defined(FEATURE_SIMD)
    ValueNum VNForSimd8Con(const simd8_t& cnsVal);
    ValueNum VNForSimd12Con(const simd12_t& cnsVal);
    ValueNum VNForSimd16Con(const simd16_t& cnsVal);
#if defined(TARGET_XARCH)
    ValueNum VNForSimd32Con(const simd32_t& cnsVal);
    ValueNum VNForSimd64Con(const simd64_t& cnsVal);
#endif // TARGET_XARCH
#if defined(FEATURE_MASKED_HW_INTRINSICS)
    ValueNum VNForSimdMaskCon(const simdmask_t& cnsVal);
#endif // FEATURE_MASKED_HW_INTRINSICS
#endif // FEATURE_SIMD
    ValueNum VNForGenericCon(var_types typ, uint8_t* cnsVal);

#ifdef TARGET_64BIT
    ValueNum VNForPtrSizeIntCon(INT64 cnsVal)
    {
        return VNForLongCon(cnsVal);
    }
#else
    ValueNum VNForPtrSizeIntCon(INT32 cnsVal)
    {
        return VNForIntCon(cnsVal);
    }
#endif

    // Packs information about the cast into an integer constant represented by the returned value number,
    // to be used as the second operand of VNF_Cast & VNF_CastOvf.
    ValueNum VNForCastOper(var_types castToType, bool srcIsUnsigned);

    // Unpacks the information stored by VNForCastOper in the constant represented by the value number.
    void GetCastOperFromVN(ValueNum vn, var_types* pCastToType, bool* pSrcIsUnsigned);

    // We keep handle values in a separate pool, so we don't confuse a handle with an int constant
    // that happens to be the same...
    ValueNum VNForHandle(ssize_t cnsVal, GenTreeFlags iconFlags);

    void AddToEmbeddedHandleMap(ssize_t embeddedHandle, ssize_t compileTimeHandle)
    {
        m_embeddedToCompileTimeHandleMap.AddOrUpdate(embeddedHandle, compileTimeHandle);
    }

    bool EmbeddedHandleMapLookup(ssize_t embeddedHandle, ssize_t* compileTimeHandle)
    {
        return m_embeddedToCompileTimeHandleMap.TryGetValue(embeddedHandle, compileTimeHandle);
    }

    void AddToFieldAddressToFieldSeqMap(ValueNum fldAddr, FieldSeq* fldSeq)
    {
        m_fieldAddressToFieldSeqMap.AddOrUpdate(fldAddr, fldSeq);
    }

    FieldSeq* GetFieldSeqFromAddress(ValueNum fldAddr)
    {
        FieldSeq* fldSeq;
        if (m_fieldAddressToFieldSeqMap.TryGetValue(fldAddr, &fldSeq))
        {
            return fldSeq;
        }
        return nullptr;
    }

    CORINFO_CLASS_HANDLE GetObjectType(ValueNum vn, bool* pIsExact, bool* pIsNonNull);

    void PeelOffsets(ValueNum* vn, target_ssize_t* offset);
    void PeelOffsetsI32(ValueNum* vn, int* offset);

    typedef JitHashTable<ValueNum, JitSmallPrimitiveKeyFuncs<ValueNum>, bool> ValueNumSet;

    class SmallValueNumSet
    {
        union
        {
            ValueNum     m_inlineElements[4];
            ValueNumSet* m_set;
        };
        unsigned m_numElements = 0;

    public:
        unsigned Count()
        {
            return m_numElements;
        }

        template <typename Func>
        void ForEach(Func func)
        {
            if (m_numElements <= ArrLen(m_inlineElements))
            {
                for (unsigned i = 0; i < m_numElements; i++)
                {
                    func(m_inlineElements[i]);
                }
            }
            else
            {
                for (ValueNum vn : ValueNumSet::KeyIteration(m_set))
                {
                    func(vn);
                }
            }
        }

        // Returns false if the value wasn't found
        bool Lookup(ValueNum vn);

        // Returns false if the value already exists
        bool Add(Compiler* comp, ValueNum vn);
    };

    enum class VNVisit
    {
        Continue,
        Abort,
    };

    ValueNum VNPhiDefToVN(const VNPhiDef& phiDef, unsigned ssaArgNum);

    //--------------------------------------------------------------------------------
    // VNVisitReachingVNs: given a VN, call the specified callback function on it and all the VNs that reach it
    //    via PHI definitions if any.
    //
    // Arguments:
    //    vn         - The VN to visit all the reaching VNs for
    //    argVisitor - The callback function to call on the vn and its PHI arguments if any
    //
    // Return Value:
    //    VNVisit::Aborted  - an argVisitor returned VNVisit::Abort, we stop the walk and return
    //    VNVisit::Continue - all argVisitor returned VNVisit::Continue
    //
    template <typename TArgVisitor>
    VNVisit VNVisitReachingVNs(ValueNum vn, TArgVisitor argVisitor)
    {
        // Fast-path: in most cases vn is not a phi definition
        if (!IsPhiDef(vn))
        {
            return argVisitor(vn);
        }
        return VNVisitReachingVNsWorker(vn, argVisitor);
    }

private:

    // Helper function for VNVisitReachingVNs
    template <typename TArgVisitor>
    VNVisit VNVisitReachingVNsWorker(ValueNum vn, TArgVisitor argVisitor)
    {
        ArrayStack<ValueNum> toVisit(m_alloc);
        toVisit.Push(vn);

        SmallValueNumSet visited;
        visited.Add(m_pComp, vn);
        while (toVisit.Height() > 0)
        {
            ValueNum vnToVisit = toVisit.Pop();

            // We need to handle nested (and, potentially, recursive) phi definitions.
            // For now, we ignore memory phi definitions.
            VNPhiDef phiDef;
            if (GetPhiDef(vnToVisit, &phiDef))
            {
                for (unsigned ssaArgNum = 0; ssaArgNum < phiDef.NumArgs; ssaArgNum++)
                {
                    ValueNum childVN = VNPhiDefToVN(phiDef, ssaArgNum);
                    if (visited.Add(m_pComp, childVN))
                    {
                        toVisit.Push(childVN);
                    }
                }
            }
            else
            {
                if (argVisitor(vnToVisit) == VNVisit::Abort)
                {
                    // The visitor wants to abort the walk.
                    return VNVisit::Abort;
                }
            }
        }
        return VNVisit::Continue;
    }

public:

    // And the single constant for an object reference type.
    static ValueNum VNForNull()
    {
        return ValueNum(SRC_Null);
    }

    // A special value number for "void" -- sometimes a type-void thing is an argument,
    // and we want the args to be non-NoVN.
    static ValueNum VNForVoid()
    {
        return ValueNum(SRC_Void);
    }

    static ValueNumPair VNPForVoid()
    {
        return ValueNumPair(VNForVoid(), VNForVoid());
    }

    // A special value number for the empty set of exceptions.
    static ValueNum VNForEmptyExcSet()
    {
        return ValueNum(SRC_EmptyExcSet);
    }

    static ValueNumPair VNPForEmptyExcSet()
    {
        return ValueNumPair(VNForEmptyExcSet(), VNForEmptyExcSet());
    }

    // Returns the value number for zero of the given "typ".
    // It has an unreached() for a "typ" that has no zero value, such as TYP_VOID.
    ValueNum VNZeroForType(var_types typ);

    // Returns the value number for a zero-initialized struct.
    ValueNum VNForZeroObj(ClassLayout* layout);

    // Returns the value number for one of the given "typ".
    // It returns NoVN for a "typ" that has no one value, such as TYP_REF.
    ValueNum VNOneForType(var_types typ);

    // Returns the value number for AllBitsSet of the given "typ".
    // It has an unreached() for a "typ" that has no all bits set value, such as TYP_VOID.
    // elementCount is used for TYP_MASK and indicates how many bits should be set
    ValueNum VNAllBitsForType(var_types typ, unsigned elementCount);

#ifdef FEATURE_SIMD
    // Returns the value number broadcast of the given "simdType" and "simdBaseType".
    ValueNum VNBroadcastForSimdType(var_types simdType, var_types simdBaseType, ValueNum valVN);

    // Returns the value number for one of the given "simdType" and "simdBaseType".
    ValueNum VNOneForSimdType(var_types simdType, var_types simdBaseType);

    // A helper function for constructing VNF_SimdType VNs.
    ValueNum VNForSimdType(unsigned simdSize, CorInfoType simdBaseJitType);

    // Returns if a value number represents NaN in all elements
    bool VNIsVectorNaN(var_types simdType, var_types simdBaseType, ValueNum valVN);

    // Returns if a value number represents negative zero in all elements
    bool VNIsVectorNegativeZero(var_types simdType, var_types simdBaseType, ValueNum valVN);
#endif // FEATURE_SIMD

    // Create or return the existimg value number representing a singleton exception set
    // for the exception value "x".
    ValueNum     VNExcSetSingleton(ValueNum x);
    ValueNumPair VNPExcSetSingleton(ValueNumPair x);

    // Returns true if the current pair of items are in ascending order and they are not duplicates.
    // Used to verify that exception sets are in ascending order when processing them.
    bool VNCheckAscending(ValueNum item, ValueNum xs1);

    // Returns the VN representing the union of the two exception sets "xs0" and "xs1".
    // These must be VNForEmptyExcSet() or applications of VNF_ExcSetCons, obeying
    // the ascending order invariant. (which is preserved in the result)
    ValueNum VNExcSetUnion(ValueNum xs0, ValueNum xs1);

    ValueNumPair VNPExcSetUnion(ValueNumPair xs0vnp, ValueNumPair xs1vnp);

    // Returns the VN representing the intersection of the two exception sets "xs0" and "xs1".
    // These must be applications of VNF_ExcSetCons or the empty set. (i.e VNForEmptyExcSet())
    // and also must be in ascending order.
    ValueNum VNExcSetIntersection(ValueNum xs0, ValueNum xs1);

    ValueNumPair VNPExcSetIntersection(ValueNumPair xs0vnp, ValueNumPair xs1vnp);

    // Returns true if every exception singleton in the vnCandidateSet is also present
    // in the vnFullSet.
    // Both arguments must be either VNForEmptyExcSet() or applications of VNF_ExcSetCons.
    bool VNExcIsSubset(ValueNum vnFullSet, ValueNum vnCandidateSet);

    bool VNPExcIsSubset(ValueNumPair vnpFullSet, ValueNumPair vnpCandidateSet);

    // Returns "true" iff "vn" is an application of "VNF_ValWithExc".
    bool VNHasExc(ValueNum vn)
    {
        VNFuncApp funcApp;
        return GetVNFunc(vn, &funcApp) && funcApp.m_func == VNF_ValWithExc;
    }

    // If vn "excSet" is "VNForEmptyExcSet()" we just return "vn"
    // otherwise we use VNExcSetUnion to combine the exception sets of both "vn" and "excSet"
    // and return that ValueNum
    ValueNum VNWithExc(ValueNum vn, ValueNum excSet);

    ValueNumPair VNPWithExc(ValueNumPair vnp, ValueNumPair excSetVNP);

    // This sets "*pvn" to the Normal value and sets "*pvnx" to Exception set value.
    // "pvnx" represents the set of all exceptions that can happen for the expression
    void VNUnpackExc(ValueNum vnWx, ValueNum* pvn, ValueNum* pvnx);

    void VNPUnpackExc(ValueNumPair vnWx, ValueNumPair* pvn, ValueNumPair* pvnx);

    // This returns the Union of exceptions from vnWx and vnExcSet
    ValueNum VNUnionExcSet(ValueNum vnWx, ValueNum vnExcSet);

    // This returns the Union of exceptions from vnpWx and vnpExcSet
    ValueNumPair VNPUnionExcSet(ValueNumPair vnpWx, ValueNumPair vnpExcSet);

    // Sets the normal value to a new unique ValueNum
    // Keeps any Exception set values
    ValueNum VNMakeNormalUnique(ValueNum vn);

    // Sets the liberal & conservative
    // Keeps any Exception set values
    ValueNumPair VNPMakeNormalUniquePair(ValueNumPair vnp);

    // A new unique value with the given exception set.
    ValueNum VNUniqueWithExc(var_types type, ValueNum vnExcSet);

    // A new unique VN pair with the given exception set pair.
    ValueNumPair VNPUniqueWithExc(var_types type, ValueNumPair vnpExcSet);

    // If "vn" is a "VNF_ValWithExc(norm, excSet)" value, returns the "norm" argument; otherwise,
    // just returns "vn".
    // The Normal value is the value number of the expression when no exceptions occurred
    ValueNum VNNormalValue(ValueNum vn);

    // Given a "vnp", get the ValueNum kind based upon vnk,
    // then call VNNormalValue on that ValueNum
    // The Normal value is the value number of the expression when no exceptions occurred
    ValueNum VNNormalValue(ValueNumPair vnp, ValueNumKind vnk);

    // Given a "vnp", get the NormalValuew for the VNK_Liberal part of that ValueNum
    // The Normal value is the value number of the expression when no exceptions occurred
    inline ValueNum VNLiberalNormalValue(ValueNumPair vnp)
    {
        return VNNormalValue(vnp, VNK_Liberal);
    }

    // Given a "vnp", get the NormalValuew for the VNK_Conservative part of that ValueNum
    // The Normal value is the value number of the expression when no exceptions occurred
    inline ValueNum VNConservativeNormalValue(ValueNumPair vnp)
    {
        return VNNormalValue(vnp, VNK_Conservative);
    }

    // Given a "vnp", get the Normal values for both the liberal and conservative parts of "vnp"
    // The Normal value is the value number of the expression when no exceptions occurred
    ValueNumPair VNPNormalPair(ValueNumPair vnp);

    // If "vn" is a "VNF_ValWithExc(norm, excSet)" value, returns the "excSet" argument; otherwise,
    // we return a special Value Number representing the empty exception set.
    // The exception set value is the value number of the set of possible exceptions.
    ValueNum VNExceptionSet(ValueNum vn);

    ValueNumPair VNPExceptionSet(ValueNumPair vn);

    // True "iff" vn is a value known to be non-null.  (For example, the result of an allocation...)
    bool IsKnownNonNull(ValueNum vn);

    // True "iff" vn is a value returned by a call to a shared static helper.
    bool IsSharedStatic(ValueNum vn);

    // VNForFunc: We have five overloads, for arities 0, 1, 2, 3 and 4
    ValueNum VNForFunc(var_types typ, VNFunc func);
    ValueNum VNForFunc(var_types typ, VNFunc func, ValueNum opVNwx);
    // This must not be used for VNF_MapSelect applications; instead use VNForMapSelect, below.
    ValueNum VNForFunc(var_types typ, VNFunc func, ValueNum op1VNwx, ValueNum op2VNwx);
    ValueNum VNForFunc(var_types typ, VNFunc func, ValueNum op1VNwx, ValueNum op2VNwx, ValueNum op3VNwx);

    // The following four-op VNForFunc is used for VNF_PtrToArrElem, elemTypeEqVN, arrVN, inxVN, fldSeqVN
    ValueNum VNForFunc(
        var_types typ, VNFunc func, ValueNum op1VNwx, ValueNum op2VNwx, ValueNum op3VNwx, ValueNum op4VNwx);

    // Skip all folding checks.
    ValueNum VNForFuncNoFolding(var_types typ, VNFunc func, ValueNum op1VNwx, ValueNum op2VNwx);

    ValueNum VNForPhiDef(var_types type, unsigned lclNum, unsigned ssaDef, ArrayStack<unsigned>& ssaArgs);
    bool     GetPhiDef(ValueNum vn, VNPhiDef* phiDef);
    bool     IsPhiDef(ValueNum vn) const;
    ValueNum VNForMemoryPhiDef(BasicBlock* block, ArrayStack<unsigned>& vns);
    bool     GetMemoryPhiDef(ValueNum vn, VNMemoryPhiDef* memoryPhiDef);

    ValueNum VNForCast(VNFunc func, ValueNum castToVN, ValueNum objVN);

    ValueNum VNForMapSelect(ValueNumKind vnk, var_types type, ValueNum map, ValueNum index);

    ValueNum VNForMapPhysicalSelect(ValueNumKind vnk, var_types type, ValueNum map, unsigned offset, unsigned size);

    ValueNum VNForMapSelectInner(ValueNumKind vnk, var_types type, ValueNum map, ValueNum index);

    // A method that does the work for VNForMapSelect and may call itself recursively.
    ValueNum VNForMapSelectWork(ValueNumKind            vnk,
                                var_types               type,
                                ValueNum                map,
                                ValueNum                index,
                                int*                    pBudget,
                                bool*                   pUsedRecursiveVN,
                                class SmallValueNumSet& loopMemoryDependencies);

    // A specialized version of VNForFunc that is used for VNF_MapStore and provides some logging when verbose is set
    ValueNum VNForMapStore(ValueNum map, ValueNum index, ValueNum value);

    ValueNum VNForMapPhysicalStore(ValueNum map, unsigned offset, unsigned size, ValueNum value);

    bool MapIsPrecise(ValueNum map) const
    {
        return (TypeOfVN(map) == TYP_HEAP) || (TypeOfVN(map) == TYP_MEM);
    }

    bool MapIsPhysical(ValueNum map) const
    {
        return !MapIsPrecise(map);
    }

    ValueNum EncodePhysicalSelector(unsigned offset, unsigned size);

    unsigned DecodePhysicalSelector(ValueNum selector, unsigned* pSize);

    ValueNum VNForFieldSelector(CORINFO_FIELD_HANDLE fieldHnd, var_types* pFieldType, unsigned* pSize);

    // These functions parallel the ones above, except that they take liberal/conservative VN pairs
    // as arguments, and return such a pair (the pair of the function applied to the liberal args, and
    // the function applied to the conservative args).
    ValueNumPair VNPairForFunc(var_types typ, VNFunc func)
    {
        ValueNumPair res;
        res.SetBoth(VNForFunc(typ, func));
        return res;
    }
    ValueNumPair VNPairForFunc(var_types typ, VNFunc func, ValueNumPair opVN)
    {
        ValueNum liberalFuncVN = VNForFunc(typ, func, opVN.GetLiberal());
        ValueNum conservativeFuncVN;

        if (opVN.BothEqual())
        {
            conservativeFuncVN = liberalFuncVN;
        }
        else
        {
            conservativeFuncVN = VNForFunc(typ, func, opVN.GetConservative());
        }
        return ValueNumPair(liberalFuncVN, conservativeFuncVN);
    }
    ValueNumPair VNPairForFunc(var_types typ, VNFunc func, ValueNumPair op1VN, ValueNumPair op2VN)
    {
        ValueNum liberalFuncVN = VNForFunc(typ, func, op1VN.GetLiberal(), op2VN.GetLiberal());
        ValueNum conservativeFuncVN;

        if (op1VN.BothEqual() && op2VN.BothEqual())
        {
            conservativeFuncVN = liberalFuncVN;
        }
        else
        {
            conservativeFuncVN = VNForFunc(typ, func, op1VN.GetConservative(), op2VN.GetConservative());
        }

        return ValueNumPair(liberalFuncVN, conservativeFuncVN);
    }
    ValueNumPair VNPairForFuncNoFolding(var_types typ, VNFunc func, ValueNumPair op1VN, ValueNumPair op2VN)
    {
        ValueNum liberalFuncVN = VNForFuncNoFolding(typ, func, op1VN.GetLiberal(), op2VN.GetLiberal());
        ValueNum conservativeFuncVN;

        if (op1VN.BothEqual() && op2VN.BothEqual())
        {
            conservativeFuncVN = liberalFuncVN;
        }
        else
        {
            conservativeFuncVN = VNForFuncNoFolding(typ, func, op1VN.GetConservative(), op2VN.GetConservative());
        }

        return ValueNumPair(liberalFuncVN, conservativeFuncVN);
    }
    ValueNumPair VNPairForFunc(var_types typ, VNFunc func, ValueNumPair op1VN, ValueNumPair op2VN, ValueNumPair op3VN)
    {
        ValueNum liberalFuncVN = VNForFunc(typ, func, op1VN.GetLiberal(), op2VN.GetLiberal(), op3VN.GetLiberal());
        ValueNum conservativeFuncVN;

        if (op1VN.BothEqual() && op2VN.BothEqual() && op3VN.BothEqual())
        {
            conservativeFuncVN = liberalFuncVN;
        }
        else
        {
            conservativeFuncVN =
                VNForFunc(typ, func, op1VN.GetConservative(), op2VN.GetConservative(), op3VN.GetConservative());
        }

        return ValueNumPair(liberalFuncVN, conservativeFuncVN);
    }
    ValueNumPair VNPairForFunc(
        var_types typ, VNFunc func, ValueNumPair op1VN, ValueNumPair op2VN, ValueNumPair op3VN, ValueNumPair op4VN)
    {
        ValueNum liberalFuncVN =
            VNForFunc(typ, func, op1VN.GetLiberal(), op2VN.GetLiberal(), op3VN.GetLiberal(), op4VN.GetLiberal());
        ValueNum conservativeFuncVN;

        if (op1VN.BothEqual() && op2VN.BothEqual() && op3VN.BothEqual() && op4VN.BothEqual())
        {
            conservativeFuncVN = liberalFuncVN;
        }
        else
        {
            conservativeFuncVN = VNForFunc(typ, func, op1VN.GetConservative(), op2VN.GetConservative(),
                                           op3VN.GetConservative(), op4VN.GetConservative());
        }

        return ValueNumPair(liberalFuncVN, conservativeFuncVN);
    }

    ValueNum     VNForExpr(BasicBlock* block, var_types type = TYP_UNKNOWN);
    ValueNumPair VNPairForExpr(BasicBlock* block, var_types type);

// This controls extra tracing of the "evaluation" of "VNF_MapSelect" functions.
#define FEATURE_VN_TRACE_APPLY_SELECTORS 1

    ValueNum VNForLoad(ValueNumKind vnk,
                       ValueNum     locationValue,
                       unsigned     locationSize,
                       var_types    loadType,
                       ssize_t      offset,
                       unsigned     loadSize);

    ValueNumPair VNPairForLoad(
        ValueNumPair locationValue, unsigned locationSize, var_types loadType, ssize_t offset, unsigned loadSize);

    ValueNum VNForStore(
        ValueNum locationValue, unsigned locationSize, ssize_t offset, unsigned storeSize, ValueNum value);

    ValueNumPair VNPairForStore(
        ValueNumPair locationValue, unsigned locationSize, ssize_t offset, unsigned storeSize, ValueNumPair value);

    static bool LoadStoreIsEntire(unsigned locationSize, ssize_t offset, unsigned indSize)
    {
        return (offset == 0) && (locationSize == indSize);
    }

    ValueNum VNForLoadStoreBitCast(ValueNum value, var_types indType, unsigned indSize);

    ValueNumPair VNPairForLoadStoreBitCast(ValueNumPair value, var_types indType, unsigned indSize);

    // Compute the ValueNumber for a cast
    ValueNum VNForCast(ValueNum  srcVN,
                       var_types castToType,
                       var_types castFromType,
                       bool      srcIsUnsigned    = false,
                       bool      hasOverflowCheck = false);

    // Compute the ValueNumberPair for a cast
    ValueNumPair VNPairForCast(ValueNumPair srcVNPair,
                               var_types    castToType,
                               var_types    castFromType,
                               bool         srcIsUnsigned    = false,
                               bool         hasOverflowCheck = false);

    ValueNum EncodeBitCastType(var_types castToType, unsigned size);

    var_types DecodeBitCastType(ValueNum castToTypeVN, unsigned* pSize);

    ValueNum VNForBitCast(ValueNum srcVN, var_types castToType, unsigned size);

    ValueNumPair VNPairForBitCast(ValueNumPair srcVNPair, var_types castToType, unsigned size);

    ValueNum VNForFieldSeq(FieldSeq* fieldSeq);

    FieldSeq* FieldSeqVNToFieldSeq(ValueNum vn);

    ValueNum ExtendPtrVN(GenTree* opA, GenTree* opB);

    ValueNum ExtendPtrVN(GenTree* opA, FieldSeq* fieldSeq, ssize_t offset);

    // Queries on value numbers.
    // All queries taking value numbers require that those value numbers are valid, that is, that
    // they have been returned by previous "VNFor..." operations.  They can assert false if this is
    // not true.

    // Returns TYP_UNKNOWN if the given value number has not been given a type.
    var_types TypeOfVN(ValueNum vn) const;

    // Returns nullptr if the given value number is not dependent on memory defined in a loop.
    class FlowGraphNaturalLoop* LoopOfVN(ValueNum vn);

    // Returns true iff the VN represents a constant.
    bool IsVNConstant(ValueNum vn);

    // Returns true iff the VN represents a (non-handle) constant.
    bool IsVNConstantNonHandle(ValueNum vn);

    // Returns true iff the VN represents an integer constant.
    bool IsVNInt32Constant(ValueNum vn);

    // Returns true if the VN represents a node that is never negative.
    bool IsVNNeverNegative(ValueNum vn);

    // Returns true if the VN represents BitOperations.Log2 pattern
    bool IsVNLog2(ValueNum vn, int* upperBound = nullptr);

    typedef SmallHashTable<ValueNum, bool, 8U> CheckedBoundVNSet;

    // Returns true if the VN is known or likely to appear as the conservative value number
    // of the length argument to a GT_BOUNDS_CHECK node.
    bool IsVNCheckedBound(ValueNum vn);

    // Returns true if the VN is known to be a cast to ulong
    bool IsVNCastToULong(ValueNum vn, ValueNum* castedOp);

    // Record that a VN is known to appear as the conservative value number of the length
    // argument to a GT_BOUNDS_CHECK node.
    void SetVNIsCheckedBound(ValueNum vn);

    // Information about the individual components of a value number representing an unsigned
    // comparison of some value against a checked bound VN.
    struct UnsignedCompareCheckedBoundInfo
    {
        unsigned cmpOper;
        ValueNum vnIdx;
        ValueNum vnBound;

        UnsignedCompareCheckedBoundInfo()
            : cmpOper(GT_NONE)
            , vnIdx(NoVN)
            , vnBound(NoVN)
        {
        }
    };

    struct CompareCheckedBoundArithInfo
    {
        // (vnBound - 1) > vnOp
        // (vnBound arrOper arrOp) cmpOper cmpOp
        ValueNum vnBound;
        unsigned arrOper;
        ValueNum arrOp;
        bool     arrOpLHS; // arrOp is on the left side of cmpOp expression
        unsigned cmpOper;
        ValueNum cmpOp;
        CompareCheckedBoundArithInfo()
            : vnBound(NoVN)
            , arrOper(GT_NONE)
            , arrOp(NoVN)
            , arrOpLHS(false)
            , cmpOper(GT_NONE)
            , cmpOp(NoVN)
        {
        }
#ifdef DEBUG
        void dump(ValueNumStore* vnStore)
        {
            vnStore->vnDump(vnStore->m_pComp, cmpOp);
            printf(" ");
            printf(vnStore->VNFuncName((VNFunc)cmpOper));
            printf(" ");
            vnStore->vnDump(vnStore->m_pComp, vnBound);
            if (arrOper != GT_NONE)
            {
                printf(vnStore->VNFuncName((VNFunc)arrOper));
                vnStore->vnDump(vnStore->m_pComp, arrOp);
            }
        }
#endif
    };

    struct ConstantBoundInfo
    {
        // 100 > vnOp
        int      constVal;
        unsigned cmpOper;
        ValueNum cmpOpVN;
        bool     isUnsigned;

        ConstantBoundInfo()
            : constVal(0)
            , cmpOper(GT_NONE)
            , cmpOpVN(NoVN)
            , isUnsigned(false)
        {
        }

#ifdef DEBUG
        void dump(ValueNumStore* vnStore)
        {
            vnStore->vnDump(vnStore->m_pComp, cmpOpVN);
            printf(" ");
            printf(vnStore->VNFuncName((VNFunc)cmpOper));
            printf(" ");
            printf("%d", constVal);
        }
#endif
    };

    // Check if "vn" is "new [] (type handle, size)"
    bool IsVNNewArr(ValueNum vn, VNFuncApp* funcApp);

    // Check if "vn" is "new [] (type handle, size) [stack allocated]"
    bool IsVNNewLocalArr(ValueNum vn, VNFuncApp* funcApp);

    // Check if "vn" IsVNNewArr and return false if arr size cannot be determined.
    bool TryGetNewArrSize(ValueNum vn, int* size);

    // Check if "vn" is "a.Length" or "a.GetLength(n)"
    bool IsVNArrLen(ValueNum vn);

    // If "vn" is VN(a.Length) or VN(a.GetLength(n)) then return VN(a); NoVN if VN(a) can't be determined.
    ValueNum GetArrForLenVn(ValueNum vn);

    // Return true with any Relop except for == and !=  and one operand has to be a 32-bit integer constant.
    bool IsVNConstantBound(ValueNum vn);

    // If "vn" is of the form "(uint)var relop cns" for any relop except for == and !=
    bool IsVNConstantBoundUnsigned(ValueNum vn);

    // If "vn" is constant bound, then populate the "info" fields for constVal, cmpOp, cmpOper.
    void GetConstantBoundInfo(ValueNum vn, ConstantBoundInfo* info);

    // If "vn" is of the form "(uint)var < (uint)len" (or equivalent) return true.
    bool IsVNUnsignedCompareCheckedBound(ValueNum vn, UnsignedCompareCheckedBoundInfo* info);

    // If "vn" is of the form "var < len" or "len <= var" return true.
    bool IsVNCompareCheckedBound(ValueNum vn);

    // If "vn" is checked bound, then populate the "info" fields for the boundVn, cmpOp, cmpOper.
    void GetCompareCheckedBound(ValueNum vn, CompareCheckedBoundArithInfo* info);

    // If "vn" is of the form "len +/- var" return true.
    bool IsVNCheckedBoundArith(ValueNum vn);

    // If "vn" is checked bound arith, then populate the "info" fields for arrOper, arrVn, arrOp.
    void GetCheckedBoundArithInfo(ValueNum vn, CompareCheckedBoundArithInfo* info);

    // If "vn" is of the form "var < len +/- k" return true.
    bool IsVNCompareCheckedBoundArith(ValueNum vn);

    // If "vn" is checked bound arith, then populate the "info" fields for cmpOp, cmpOper.
    void GetCompareCheckedBoundArithInfo(ValueNum vn, CompareCheckedBoundArithInfo* info);

    // Returns the flags on the current handle. GTF_ICON_SCOPE_HDL for example.
    GenTreeFlags GetHandleFlags(ValueNum vn);

    // Returns true iff the VN represents a handle constant.
    bool IsVNHandle(ValueNum vn);

    // Returns true iff the VN represents a specific handle constant.
    bool IsVNHandle(ValueNum vn, GenTreeFlags flag);

    // Returns true iff the VN represents an object handle constant.
    bool IsVNObjHandle(ValueNum vn);

    // Returns true iff the VN represents a Type handle constant.
    bool IsVNTypeHandle(ValueNum vn);

    // Returns true iff the VN represents a relop
    bool IsVNRelop(ValueNum vn);

    enum class VN_RELATION_KIND
    {
        VRK_Inferred,   // (x ?  y)
        VRK_Same,       // (x >  y)
        VRK_Swap,       // (y >  x)
        VRK_Reverse,    // (x <= y)
        VRK_SwapReverse // (y >= x)
    };

#ifdef DEBUG
    static const char* VNRelationString(VN_RELATION_KIND vrk);
#endif

    // Given VN(x > y), return VN(y > x), VN(x <= y) or VN(y >= x)
    //
    // If vn is not a relop, return NoVN.
    //
    ValueNum GetRelatedRelop(ValueNum vn, VN_RELATION_KIND vrk);

    // Return VNFunc for swapped relop, or VNF_MemOpaque if the function
    // is not a relop.
    static VNFunc SwapRelop(VNFunc vnf);

    // Returns "true" iff "vnf" is a comparison (and thus binary) operator.
    static bool VNFuncIsComparison(VNFunc vnf);

    // Returns "true" iff "vnf" is a signed comparison (and thus binary) operator.
    static bool VNFuncIsSignedComparison(VNFunc vnf);

    // Convert a vartype_t to the value number's storage type for that vartype_t.
    // For example, ValueNum of type TYP_LONG are stored in a map of INT64 variables.
    // Lang is the language (C++) type for the corresponding vartype_t.
    template <int N>
    struct VarTypConv
    {
    };

private:
    struct Chunk;

    template <typename T>
    static T CoerceTypRefToT(Chunk* c, unsigned offset);

    // Get the actual value and coerce the actual type c->m_typ to the wanted type T.
    template <typename T>
    FORCEINLINE T SafeGetConstantValue(Chunk* c, unsigned offset);

    template <typename T>
    T ConstantValueInternal(ValueNum vn DEBUGARG(bool coerce))
    {
        Chunk* c = m_chunks.GetNoExpand(GetChunkNum(vn));
        assert(c->m_attribs == CEA_Const || c->m_attribs == CEA_Handle);

        unsigned offset = ChunkOffset(vn);

        switch (c->m_typ)
        {
            case TYP_REF:
                assert((offset <= 1) || IsVNObjHandle(vn)); // Null, exception or nongc obj handle
                FALLTHROUGH;

            case TYP_BYREF:

#ifdef _MSC_VER

                assert((&typeid(T) == &typeid(size_t)) ||
                       (&typeid(T) == &typeid(ssize_t))); // We represent ref/byref constants as size_t/ssize_t

#endif // _MSC_VER

                FALLTHROUGH;

            case TYP_INT:
            case TYP_LONG:
            case TYP_FLOAT:
            case TYP_DOUBLE:
                if (c->m_attribs == CEA_Handle)
                {
                    C_ASSERT(offsetof(VNHandle, m_cnsVal) == 0);
                    return (T) reinterpret_cast<VNHandle*>(c->m_defs)[offset].m_cnsVal;
                }
#ifdef DEBUG
                if (!coerce)
                {
                    T val1 = reinterpret_cast<T*>(c->m_defs)[offset];
                    T val2 = SafeGetConstantValue<T>(c, offset);

                    // Detect if there is a mismatch between the VN storage type and explicitly
                    // passed-in type T.
                    bool mismatch = false;
                    if (varTypeIsFloating(c->m_typ))
                    {
                        mismatch = (memcmp(&val1, &val2, sizeof(val1)) != 0);
                    }
                    else
                    {
                        mismatch = (val1 != val2);
                    }

                    if (mismatch)
                    {
                        assert(
                            !"Called ConstantValue<T>(vn), but type(T) != type(vn); Use CoercedConstantValue instead.");
                    }
                }
#endif
                return SafeGetConstantValue<T>(c, offset);

            default:
                assert(false); // We do not record constants of this typ.
                return (T)0;
        }
    }

public:
    // Requires that "vn" is a constant, and that its type is compatible with the explicitly passed
    // type "T". Also, note that "T" has to have an accurate storage size of the TypeOfVN(vn).
    template <typename T>
    T ConstantValue(ValueNum vn)
    {
        return ConstantValueInternal<T>(vn DEBUGARG(false));
    }

    // Requires that "vn" is a constant, and that its type can be coerced to the explicitly passed
    // type "T".
    template <typename T>
    T CoercedConstantValue(ValueNum vn)
    {
        return ConstantValueInternal<T>(vn DEBUGARG(true));
    }

    template <typename T>
    bool IsVNIntegralConstant(ValueNum vn, T* value)
    {
        if (!IsVNConstant(vn) || !varTypeIsIntegral(TypeOfVN(vn)))
        {
            *value = 0;
            return false;
        }
        ssize_t val = CoercedConstantValue<ssize_t>(vn);
        if (FitsIn<T>(val))
        {
            *value = static_cast<T>(val);
            return true;
        }
        *value = 0;
        return false;
    }

    CORINFO_OBJECT_HANDLE ConstantObjHandle(ValueNum vn)
    {
        assert(IsVNObjHandle(vn));
        return reinterpret_cast<CORINFO_OBJECT_HANDLE>(CoercedConstantValue<size_t>(vn));
    }

    // Requires "mthFunc" to be an intrinsic math function (one of the allowable values for the "gtMath" field
    // of a GenTreeMath node).  For unary ops, return the value number for the application of this function to
    // "arg0VN". For binary ops, return the value number for the application of this function to "arg0VN" and
    // "arg1VN".

    ValueNum EvalMathFuncUnary(var_types typ, NamedIntrinsic mthFunc, ValueNum arg0VN);

    ValueNum EvalMathFuncBinary(var_types typ, NamedIntrinsic mthFunc, ValueNum arg0VN, ValueNum arg1VN);

    ValueNumPair EvalMathFuncUnary(var_types typ, NamedIntrinsic mthFunc, ValueNumPair arg0VNP)
    {
        return ValueNumPair(EvalMathFuncUnary(typ, mthFunc, arg0VNP.GetLiberal()),
                            EvalMathFuncUnary(typ, mthFunc, arg0VNP.GetConservative()));
    }

    ValueNumPair EvalMathFuncBinary(var_types typ, NamedIntrinsic mthFunc, ValueNumPair arg0VNP, ValueNumPair arg1VNP)
    {
        return ValueNumPair(EvalMathFuncBinary(typ, mthFunc, arg0VNP.GetLiberal(), arg1VNP.GetLiberal()),
                            EvalMathFuncBinary(typ, mthFunc, arg0VNP.GetConservative(), arg1VNP.GetConservative()));
    }

#if defined(FEATURE_HW_INTRINSICS)
    ValueNum EvalHWIntrinsicFunUnary(GenTreeHWIntrinsic* tree, VNFunc func, ValueNum arg0VN, ValueNum resultTypeVN);

    ValueNum EvalHWIntrinsicFunBinary(
        GenTreeHWIntrinsic* tree, VNFunc func, ValueNum arg0VN, ValueNum arg1VN, ValueNum resultTypeVN);

    ValueNum EvalHWIntrinsicFunTernary(GenTreeHWIntrinsic* tree,
                                       VNFunc              func,
                                       ValueNum            arg0VN,
                                       ValueNum            arg1VN,
                                       ValueNum            arg2VN,
                                       ValueNum            resultTypeVN);
#endif // FEATURE_HW_INTRINSICS

    // Returns "true" iff "vn" represents a function application.
    bool IsVNFunc(ValueNum vn);

    // If "vn" represents a function application, returns "true" and set "*funcApp" to
    // the function application it represents; otherwise, return "false."
    bool GetVNFunc(ValueNum vn, VNFuncApp* funcApp);

    // Returns "true" iff "vn" is a function application of the form "func(op1, op2)".
    bool IsVNBinFunc(ValueNum vn, VNFunc func, ValueNum* op1 = nullptr, ValueNum* op2 = nullptr);

    // Returns "true" iff "vn" is a function application for a HWIntrinsic
    bool IsVNHWIntrinsicFunc(ValueNum        vn,
                             NamedIntrinsic* intrinsicId,
                             unsigned*       simdSize,
                             CorInfoType*    simdBaseJitType);

    // Returns "true" iff "vn" is a function application of the form "func(op, cns)"
    // the cns can be on the left side if the function is commutative.
    template <typename T>
    bool IsVNBinFuncWithConst(ValueNum vn, VNFunc func, ValueNum* op, T* cns)
    {
        T        opCns;
        ValueNum op1, op2;
        if (IsVNBinFunc(vn, func, &op1, &op2))
        {
            if (IsVNIntegralConstant(op2, &opCns))
            {
                if (op != nullptr)
                    *op = op1;
                if (cns != nullptr)
                    *cns = opCns;
                return true;
            }
            else if (VNFuncIsCommutative(func) && IsVNIntegralConstant(op1, &opCns))
            {
                if (op != nullptr)
                    *op = op2;
                if (cns != nullptr)
                    *cns = opCns;
                return true;
            }
        }
        return false;
    }

    // Returns "true" iff "vn" is a valid value number -- one that has been previously returned.
    bool VNIsValid(ValueNum vn);

#ifdef DEBUG
// This controls whether we recursively call vnDump on function arguments.
#define FEATURE_VN_DUMP_FUNC_ARGS 0

    // Prints, to standard out, a representation of "vn".
    void vnDump(Compiler* comp, ValueNum vn, bool isPtr = false);

    // Requires "fieldSeq" to be a field sequence VN.
    // Prints a representation (comma-separated list of field names) on standard out.
    void vnDumpFieldSeq(Compiler* comp, ValueNum fieldSeqVN);

    // Requires "mapSelect" to be a map select VNFuncApp.
    // Prints a representation of a MapSelect operation on standard out.
    void vnDumpMapSelect(Compiler* comp, VNFuncApp* mapSelect);

    // Requires "mapStore" to be a map store VNFuncApp.
    // Prints a representation of a MapStore operation on standard out.
    void vnDumpMapStore(Compiler* comp, VNFuncApp* mapStore);

    void vnDumpPhysicalSelector(ValueNum selector);

    void vnDumpMapPhysicalStore(Compiler* comp, VNFuncApp* mapPhysicalStore);

    // Requires "memOpaque" to be a mem opaque VNFuncApp
    // Prints a representation of a MemOpaque state on standard out.
    void vnDumpMemOpaque(Compiler* comp, VNFuncApp* memOpaque);

    // Requires "valWithExc" to be a value with an exception set VNFuncApp.
    // Prints a representation of the exception set on standard out.
    void vnDumpValWithExc(Compiler* comp, VNFuncApp* valWithExc);

    // Requires vn to be VNF_ExcSetCons or VNForEmptyExcSet().
    void vnDumpExc(Compiler* comp, ValueNum vn);

    // Requires "excSeq" to be a ExcSetCons sequence.
    // Prints a representation of the set of exceptions on standard out.
    void vnDumpExcSeq(Compiler* comp, VNFuncApp* excSeq, bool isHead);

#ifdef FEATURE_SIMD
    // Requires "simdType" to be a VNF_SimdType VNFuncApp.
    // Prints a representation (comma-separated list of field names) on standard out.
    void vnDumpSimdType(Compiler* comp, VNFuncApp* simdType);
#endif // FEATURE_SIMD

    // Requires "castVN" to represent VNF_Cast or VNF_CastOvf.
    // Prints the cast's representation mirroring GT_CAST's dump format.
    void vnDumpCast(Compiler* comp, ValueNum castVN);

    void vnDumpBitCast(Compiler* comp, VNFuncApp* bitCast);

    // Requires "zeroObj" to be a VNF_ZeroObj. Prints its representation.
    void vnDumpZeroObj(Compiler* comp, VNFuncApp* zeroObj);

    // Returns the string name of "vnf".
    static const char* VNFuncName(VNFunc vnf);
    // Used in the implementation of the above.
    static const char* VNFuncNameArr[];

    // Returns a type name used for "maps", i. e. displays TYP_UNDEF and TYP_UNKNOWN as TYP_MEM and TYP_HEAP.
    static const char* VNMapTypeName(var_types type);

    // Returns the string name of "vn" when it is a reserved value number, nullptr otherwise
    static const char* reservedName(ValueNum vn);
#endif // DEBUG

    // Returns true if "vn" is a reserved value number
    static bool isReservedVN(ValueNum);

private:
    struct VNDefFuncAppFlexible
    {
        VNFunc   m_func;
        ValueNum m_args[];
    };

    template <size_t NumArgs>
    struct VNDefFuncApp
    {
        VNFunc   m_func;
        ValueNum m_args[NumArgs];

        VNDefFuncApp()
            : m_func(VNF_COUNT)
        {
            for (size_t i = 0; i < NumArgs; i++)
            {
                m_args[i] = ValueNumStore::NoVN;
            }
        }

        template <typename... VNs>
        VNDefFuncApp(VNFunc func, VNs... vns)
            : m_func(func)
            , m_args{vns...}
        {
            static_assert_no_msg(NumArgs == sizeof...(VNs));
        }

        bool operator==(const VNDefFuncApp& y) const
        {
            bool result = m_func == y.m_func;
            // Intentionally written without early-out or MSVC cannot unroll this.
            for (size_t i = 0; i < NumArgs; i++)
            {
                result = result && m_args[i] == y.m_args[i];
            }

            return result;
        }
    };

    // We will allocate value numbers in "chunks".  Each chunk will have the same type and "constness".
    static const unsigned LogChunkSize    = 6;
    static const unsigned ChunkSize       = 1 << LogChunkSize;
    static const unsigned ChunkOffsetMask = ChunkSize - 1;

    // A "ChunkNum" is a zero-based index naming a chunk in the Store, or else the special "NoChunk" value.
    typedef UINT32        ChunkNum;
    static const ChunkNum NoChunk = UINT32_MAX;

    // Returns the ChunkNum of the Chunk that holds "vn" (which is required to be a valid
    // value number, i.e., one returned by some VN-producing method of this class).
    static ChunkNum GetChunkNum(ValueNum vn)
    {
        return vn >> LogChunkSize;
    }

    // Returns the offset of the given "vn" within its chunk.
    static unsigned ChunkOffset(ValueNum vn)
    {
        return vn & ChunkOffsetMask;
    }

    // The base VN of the next chunk to be allocated.  Should always be a multiple of ChunkSize.
    ValueNum m_nextChunkBase;

    enum ChunkExtraAttribs : BYTE
    {
        CEA_Const,        // This chunk contains constant values.
        CEA_Handle,       // This chunk contains handle constants.
        CEA_PhiDef,       // This contains pointers to VNPhiDef.
        CEA_MemoryPhiDef, // This contains pointers to VNMemoryPhiDef.
        CEA_Func0,        // Represents functions of arity 0.
        CEA_Func1,        // ...arity 1.
        CEA_Func2,        // ...arity 2.
        CEA_Func3,        // ...arity 3.
        CEA_Func4,        // ...arity 4.
        CEA_Count
    };

    // A "Chunk" holds "ChunkSize" value numbers, starting at "m_baseVN".  All of these share the same
    // "m_typ" and "m_attribs".  These properties determine the interpretation of "m_defs", as discussed below.
    struct Chunk
    {
        // If "m_defs" is non-null, it is an array of size ChunkSize, whose element type is determined by the other
        // members. The "m_numUsed" field indicates the number of elements of "m_defs" that are already consumed (the
        // next one to allocate).
        void*    m_defs;
        unsigned m_numUsed;

        // The value number of the first VN in the chunk.
        ValueNum m_baseVN;

        // The common attributes of this chunk.
        var_types         m_typ;
        ChunkExtraAttribs m_attribs;

        // Initialize a chunk, starting at "*baseVN", for the given "typ", and "attribs", using "alloc" for allocations.
        // (Increments "*baseVN" by ChunkSize.)
        Chunk(CompAllocator alloc, ValueNum* baseVN, var_types typ, ChunkExtraAttribs attribs);

        // Requires that "m_numUsed < ChunkSize."  Returns the offset of the allocated VN within the chunk; the
        // actual VN is this added to the "m_baseVN" of the chunk.
        unsigned AllocVN()
        {
            assert(m_numUsed < ChunkSize);
            return m_numUsed++;
        }

        VNDefFuncAppFlexible* PointerToFuncApp(unsigned offsetWithinChunk, unsigned numArgs)
        {
            assert((m_attribs >= CEA_Func0) && (m_attribs <= CEA_Func4));
            assert(numArgs == (unsigned)(m_attribs - CEA_Func0));
            static_assert_no_msg(sizeof(VNDefFuncAppFlexible) == sizeof(VNFunc));
            return reinterpret_cast<VNDefFuncAppFlexible*>(
                (char*)m_defs + offsetWithinChunk * (sizeof(VNDefFuncAppFlexible) + sizeof(ValueNum) * numArgs));
        }

        template <int N>
        struct Alloc
        {
            typedef typename ValueNumStore::VarTypConv<N>::Type Type;
        };
    };

    struct VNHandle : public JitKeyFuncsDefEquals<VNHandle>
    {
        ssize_t      m_cnsVal;
        GenTreeFlags m_flags;
        // Don't use a constructor to use the default copy constructor for hashtable rehash.
        static void Initialize(VNHandle* handle, ssize_t m_cnsVal, GenTreeFlags m_flags)
        {
            handle->m_cnsVal = m_cnsVal;
            handle->m_flags  = m_flags;
        }
        bool operator==(const VNHandle& y) const
        {
            return m_cnsVal == y.m_cnsVal && m_flags == y.m_flags;
        }
        static unsigned GetHashCode(const VNHandle& val)
        {
            return static_cast<unsigned>(val.m_cnsVal);
        }
    };

    // When we evaluate "select(m, i)", if "m" is a the value of a phi definition, we look at
    // all the values of the phi args, and see if doing the "select" on each of them yields identical
    // results.  If so, that is the result of the entire "select" form.  We have to be careful, however,
    // because phis may be recursive in the presence of loop structures -- the VN for the phi may be (or be
    // part of the definition of) the VN's of some of the arguments.  But there will be at least one
    // argument that does *not* depend on the outer phi VN -- after all, we had to get into the loop somehow.
    // So we have to be careful about breaking infinite recursion.  We can ignore "recursive" results -- if all the
    // non-recursive results are the same, the recursion indicates that the loop structure didn't alter the result.
    // This stack represents the set of outer phis such that select(phi, ind) is being evaluated.
    JitExpandArrayStack<VNDefFuncApp<2>> m_fixedPointMapSels;

#ifdef DEBUG
    // Returns "true" iff "m_fixedPointMapSels" is non-empty, and it's top element is
    // "select(map, index)".
    bool FixedPointMapSelsTopHasValue(ValueNum map, ValueNum index);
#endif

    // Returns true if "sel(map, ind)" is a member of "m_fixedPointMapSels".
    bool SelectIsBeingEvaluatedRecursively(ValueNum map, ValueNum ind);

    // This is the set of value numbers that have been flagged as arguments to bounds checks, in the length position.
    CheckedBoundVNSet m_checkedBoundVNs;

    // This is a map from "chunk number" to the attributes of the chunk.
    JitExpandArrayStack<Chunk*> m_chunks;

    // These entries indicate the current allocation chunk, if any, for each valid combination of <var_types,
    // ChunkExtraAttribute>.
    // If the value is NoChunk, it indicates that there is no current allocation chunk for that pair, otherwise
    // it is the index in "m_chunks" of a chunk with the given attributes, in which the next allocation should
    // be attempted.
    ChunkNum m_curAllocChunk[TYP_COUNT][CEA_Count + 1];

    // Returns a (pointer to a) chunk in which a new value number may be allocated.
    Chunk* GetAllocChunk(var_types typ, ChunkExtraAttribs attribs);

    // First, we need mechanisms for mapping from constants to value numbers.
    // For small integers, we'll use an array.
    static const int      SmallIntConstMin = -1;
    static const int      SmallIntConstMax = 10;
    static const unsigned SmallIntConstNum = SmallIntConstMax - SmallIntConstMin + 1;
    static bool           IsSmallIntConst(int i)
    {
        return SmallIntConstMin <= i && i <= SmallIntConstMax;
    }
    ValueNum m_VNsForSmallIntConsts[SmallIntConstNum];

    struct ValueNumList
    {
        ValueNum      vn;
        ValueNumList* next;
        ValueNumList(const ValueNum& v, ValueNumList* n = nullptr)
            : vn(v)
            , next(n)
        {
        }
    };

    // Keeps track of value numbers that are integer constants and also handles (GTG_ICON_HDL_MASK.)
    ValueNumList* m_intConHandles;

    typedef VNMap<INT32> IntToValueNumMap;
    IntToValueNumMap*    m_intCnsMap;
    IntToValueNumMap*    GetIntCnsMap()
    {
        if (m_intCnsMap == nullptr)
        {
            m_intCnsMap = new (m_alloc) IntToValueNumMap(m_alloc);
        }
        return m_intCnsMap;
    }

    typedef VNMap<INT64> LongToValueNumMap;
    LongToValueNumMap*   m_longCnsMap;
    LongToValueNumMap*   GetLongCnsMap()
    {
        if (m_longCnsMap == nullptr)
        {
            m_longCnsMap = new (m_alloc) LongToValueNumMap(m_alloc);
        }
        return m_longCnsMap;
    }

    typedef VNMap<VNHandle, VNHandle> HandleToValueNumMap;
    HandleToValueNumMap*              m_handleMap;
    HandleToValueNumMap*              GetHandleMap()
    {
        if (m_handleMap == nullptr)
        {
            m_handleMap = new (m_alloc) HandleToValueNumMap(m_alloc);
        }
        return m_handleMap;
    }

    typedef SmallHashTable<ssize_t, ssize_t> EmbeddedToCompileTimeHandleMap;
    EmbeddedToCompileTimeHandleMap           m_embeddedToCompileTimeHandleMap;

    typedef SmallHashTable<ValueNum, FieldSeq*> FieldAddressToFieldSeqMap;
    FieldAddressToFieldSeqMap                   m_fieldAddressToFieldSeqMap;

    struct LargePrimitiveKeyFuncsFloat : public JitLargePrimitiveKeyFuncs<float>
    {
        static bool Equals(float x, float y)
        {
            return *(unsigned*)&x == *(unsigned*)&y;
        }
    };

    typedef VNMap<float, LargePrimitiveKeyFuncsFloat> FloatToValueNumMap;
    FloatToValueNumMap*                               m_floatCnsMap;
    FloatToValueNumMap*                               GetFloatCnsMap()
    {
        if (m_floatCnsMap == nullptr)
        {
            m_floatCnsMap = new (m_alloc) FloatToValueNumMap(m_alloc);
        }
        return m_floatCnsMap;
    }

    // In the JIT we need to distinguish -0.0 and 0.0 for optimizations.
    struct LargePrimitiveKeyFuncsDouble : public JitLargePrimitiveKeyFuncs<double>
    {
        static bool Equals(double x, double y)
        {
            return *(int64_t*)&x == *(int64_t*)&y;
        }
    };

    typedef VNMap<double, LargePrimitiveKeyFuncsDouble> DoubleToValueNumMap;
    DoubleToValueNumMap*                                m_doubleCnsMap;
    DoubleToValueNumMap*                                GetDoubleCnsMap()
    {
        if (m_doubleCnsMap == nullptr)
        {
            m_doubleCnsMap = new (m_alloc) DoubleToValueNumMap(m_alloc);
        }
        return m_doubleCnsMap;
    }

    typedef VNMap<size_t> ByrefToValueNumMap;
    ByrefToValueNumMap*   m_byrefCnsMap;
    ByrefToValueNumMap*   GetByrefCnsMap()
    {
        if (m_byrefCnsMap == nullptr)
        {
            m_byrefCnsMap = new (m_alloc) ByrefToValueNumMap(m_alloc);
        }
        return m_byrefCnsMap;
    }

#if defined(FEATURE_SIMD)
    struct Simd8PrimitiveKeyFuncs : public JitKeyFuncsDefEquals<simd8_t>
    {
        static bool Equals(const simd8_t& x, const simd8_t& y)
        {
            return x == y;
        }

        static unsigned GetHashCode(const simd8_t& val)
        {
            unsigned hash = 0;

            hash = static_cast<unsigned>(hash ^ val.u32[0]);
            hash = static_cast<unsigned>(hash ^ val.u32[1]);

            return hash;
        }
    };

    typedef VNMap<simd8_t, Simd8PrimitiveKeyFuncs> Simd8ToValueNumMap;
    Simd8ToValueNumMap*                            m_simd8CnsMap;
    Simd8ToValueNumMap*                            GetSimd8CnsMap()
    {
        if (m_simd8CnsMap == nullptr)
        {
            m_simd8CnsMap = new (m_alloc) Simd8ToValueNumMap(m_alloc);
        }
        return m_simd8CnsMap;
    }

    struct Simd12PrimitiveKeyFuncs : public JitKeyFuncsDefEquals<simd12_t>
    {
        static bool Equals(const simd12_t& x, const simd12_t& y)
        {
            return x == y;
        }

        static unsigned GetHashCode(const simd12_t& val)
        {
            unsigned hash = 0;

            hash = static_cast<unsigned>(hash ^ val.u32[0]);
            hash = static_cast<unsigned>(hash ^ val.u32[1]);
            hash = static_cast<unsigned>(hash ^ val.u32[2]);

            return hash;
        }
    };

    typedef VNMap<simd12_t, Simd12PrimitiveKeyFuncs> Simd12ToValueNumMap;
    Simd12ToValueNumMap*                             m_simd12CnsMap;
    Simd12ToValueNumMap*                             GetSimd12CnsMap()
    {
        if (m_simd12CnsMap == nullptr)
        {
            m_simd12CnsMap = new (m_alloc) Simd12ToValueNumMap(m_alloc);
        }
        return m_simd12CnsMap;
    }

    struct Simd16PrimitiveKeyFuncs : public JitKeyFuncsDefEquals<simd16_t>
    {
        static bool Equals(const simd16_t& x, const simd16_t& y)
        {
            return x == y;
        }

        static unsigned GetHashCode(const simd16_t& val)
        {
            unsigned hash = 0;

            hash = static_cast<unsigned>(hash ^ val.u32[0]);
            hash = static_cast<unsigned>(hash ^ val.u32[1]);
            hash = static_cast<unsigned>(hash ^ val.u32[2]);
            hash = static_cast<unsigned>(hash ^ val.u32[3]);

            return hash;
        }
    };

    typedef VNMap<simd16_t, Simd16PrimitiveKeyFuncs> Simd16ToValueNumMap;
    Simd16ToValueNumMap*                             m_simd16CnsMap;
    Simd16ToValueNumMap*                             GetSimd16CnsMap()
    {
        if (m_simd16CnsMap == nullptr)
        {
            m_simd16CnsMap = new (m_alloc) Simd16ToValueNumMap(m_alloc);
        }
        return m_simd16CnsMap;
    }

#if defined(TARGET_XARCH)
    struct Simd32PrimitiveKeyFuncs : public JitKeyFuncsDefEquals<simd32_t>
    {
        static bool Equals(const simd32_t& x, const simd32_t& y)
        {
            return x == y;
        }

        static unsigned GetHashCode(const simd32_t& val)
        {
            unsigned hash = 0;

            hash = static_cast<unsigned>(hash ^ val.u32[0]);
            hash = static_cast<unsigned>(hash ^ val.u32[1]);
            hash = static_cast<unsigned>(hash ^ val.u32[2]);
            hash = static_cast<unsigned>(hash ^ val.u32[3]);
            hash = static_cast<unsigned>(hash ^ val.u32[4]);
            hash = static_cast<unsigned>(hash ^ val.u32[5]);
            hash = static_cast<unsigned>(hash ^ val.u32[6]);
            hash = static_cast<unsigned>(hash ^ val.u32[7]);

            return hash;
        }
    };

    typedef VNMap<simd32_t, Simd32PrimitiveKeyFuncs> Simd32ToValueNumMap;
    Simd32ToValueNumMap*                             m_simd32CnsMap;
    Simd32ToValueNumMap*                             GetSimd32CnsMap()
    {
        if (m_simd32CnsMap == nullptr)
        {
            m_simd32CnsMap = new (m_alloc) Simd32ToValueNumMap(m_alloc);
        }
        return m_simd32CnsMap;
    }

    struct Simd64PrimitiveKeyFuncs : public JitKeyFuncsDefEquals<simd64_t>
    {
        static bool Equals(const simd64_t& x, const simd64_t& y)
        {
            return x == y;
        }

        static unsigned GetHashCode(const simd64_t& val)
        {
            unsigned hash = 0;

            hash = static_cast<unsigned>(hash ^ val.u32[0]);
            hash = static_cast<unsigned>(hash ^ val.u32[1]);
            hash = static_cast<unsigned>(hash ^ val.u32[2]);
            hash = static_cast<unsigned>(hash ^ val.u32[3]);
            hash = static_cast<unsigned>(hash ^ val.u32[4]);
            hash = static_cast<unsigned>(hash ^ val.u32[5]);
            hash = static_cast<unsigned>(hash ^ val.u32[6]);
            hash = static_cast<unsigned>(hash ^ val.u32[7]);
            hash = static_cast<unsigned>(hash ^ val.u32[8]);
            hash = static_cast<unsigned>(hash ^ val.u32[9]);
            hash = static_cast<unsigned>(hash ^ val.u32[10]);
            hash = static_cast<unsigned>(hash ^ val.u32[11]);
            hash = static_cast<unsigned>(hash ^ val.u32[12]);
            hash = static_cast<unsigned>(hash ^ val.u32[13]);
            hash = static_cast<unsigned>(hash ^ val.u32[14]);
            hash = static_cast<unsigned>(hash ^ val.u32[15]);

            return hash;
        }
    };

    typedef VNMap<simd64_t, Simd64PrimitiveKeyFuncs> Simd64ToValueNumMap;
    Simd64ToValueNumMap*                             m_simd64CnsMap;
    Simd64ToValueNumMap*                             GetSimd64CnsMap()
    {
        if (m_simd64CnsMap == nullptr)
        {
            m_simd64CnsMap = new (m_alloc) Simd64ToValueNumMap(m_alloc);
        }
        return m_simd64CnsMap;
    }
#endif // TARGET_XARCH

#if defined(FEATURE_MASKED_HW_INTRINSICS)
    struct SimdMaskPrimitiveKeyFuncs : public JitKeyFuncsDefEquals<simdmask_t>
    {
        static bool Equals(const simdmask_t& x, const simdmask_t& y)
        {
            return x == y;
        }

        static unsigned GetHashCode(const simdmask_t& val)
        {
            unsigned hash = 0;

            hash = static_cast<unsigned>(hash ^ val.u32[0]);
            hash = static_cast<unsigned>(hash ^ val.u32[1]);

            return hash;
        }
    };

    typedef VNMap<simdmask_t, SimdMaskPrimitiveKeyFuncs> SimdMaskToValueNumMap;
    SimdMaskToValueNumMap*                               m_simdMaskCnsMap;
    SimdMaskToValueNumMap*                               GetSimdMaskCnsMap()
    {
        if (m_simdMaskCnsMap == nullptr)
        {
            m_simdMaskCnsMap = new (m_alloc) SimdMaskToValueNumMap(m_alloc);
        }
        return m_simdMaskCnsMap;
    }
#endif // FEATURE_MASKED_HW_INTRINSICS
#endif // FEATURE_SIMD

    template <size_t NumArgs>
    struct VNDefFuncAppKeyFuncs : public JitKeyFuncsDefEquals<VNDefFuncApp<NumArgs>>
    {
        static unsigned GetHashCode(const VNDefFuncApp<NumArgs>& val)
        {
            unsigned hashCode = val.m_func;
            for (size_t i = 0; i < NumArgs; i++)
            {
                hashCode = (hashCode << 8) | (hashCode >> 24);
                hashCode ^= val.m_args[i];
            }

            return hashCode;
        }
    };

    typedef VNMap<VNFunc> VNFunc0ToValueNumMap;
    VNFunc0ToValueNumMap* m_VNFunc0Map;
    VNFunc0ToValueNumMap* GetVNFunc0Map()
    {
        if (m_VNFunc0Map == nullptr)
        {
            m_VNFunc0Map = new (m_alloc) VNFunc0ToValueNumMap(m_alloc);
        }
        return m_VNFunc0Map;
    }

    typedef VNMap<VNDefFuncApp<1>, VNDefFuncAppKeyFuncs<1>> VNFunc1ToValueNumMap;
    VNFunc1ToValueNumMap*                                   m_VNFunc1Map;
    VNFunc1ToValueNumMap*                                   GetVNFunc1Map()
    {
        if (m_VNFunc1Map == nullptr)
        {
            m_VNFunc1Map = new (m_alloc) VNFunc1ToValueNumMap(m_alloc);
        }
        return m_VNFunc1Map;
    }

    typedef VNMap<VNDefFuncApp<2>, VNDefFuncAppKeyFuncs<2>> VNFunc2ToValueNumMap;
    VNFunc2ToValueNumMap*                                   m_VNFunc2Map;
    VNFunc2ToValueNumMap*                                   GetVNFunc2Map()
    {
        if (m_VNFunc2Map == nullptr)
        {
            m_VNFunc2Map = new (m_alloc) VNFunc2ToValueNumMap(m_alloc);
        }
        return m_VNFunc2Map;
    }

    typedef VNMap<VNDefFuncApp<3>, VNDefFuncAppKeyFuncs<3>> VNFunc3ToValueNumMap;
    VNFunc3ToValueNumMap*                                   m_VNFunc3Map;
    VNFunc3ToValueNumMap*                                   GetVNFunc3Map()
    {
        if (m_VNFunc3Map == nullptr)
        {
            m_VNFunc3Map = new (m_alloc) VNFunc3ToValueNumMap(m_alloc);
        }
        return m_VNFunc3Map;
    }

    typedef VNMap<VNDefFuncApp<4>, VNDefFuncAppKeyFuncs<4>> VNFunc4ToValueNumMap;
    VNFunc4ToValueNumMap*                                   m_VNFunc4Map;
    VNFunc4ToValueNumMap*                                   GetVNFunc4Map()
    {
        if (m_VNFunc4Map == nullptr)
        {
            m_VNFunc4Map = new (m_alloc) VNFunc4ToValueNumMap(m_alloc);
        }
        return m_VNFunc4Map;
    }

    class MapSelectWorkCacheEntry
    {
        union
        {
            ValueNum* m_memoryDependencies;
            ValueNum  m_inlineMemoryDependencies[sizeof(ValueNum*) / sizeof(ValueNum)];
        };

        unsigned m_numMemoryDependencies = 0;

    public:
        ValueNum Result;

        void SetMemoryDependencies(Compiler* comp, class SmallValueNumSet& deps);
        void GetMemoryDependencies(Compiler* comp, class SmallValueNumSet& deps);
    };

    typedef JitHashTable<VNDefFuncApp<2>, VNDefFuncAppKeyFuncs<2>, MapSelectWorkCacheEntry> MapSelectWorkCache;
    MapSelectWorkCache* m_mapSelectWorkCache = nullptr;
    MapSelectWorkCache* GetMapSelectWorkCache()
    {
        if (m_mapSelectWorkCache == nullptr)
        {
            m_mapSelectWorkCache = new (m_alloc) MapSelectWorkCache(m_alloc);
        }
        return m_mapSelectWorkCache;
    }

    // We reserve Chunk 0 for "special" VNs.
    enum SpecialRefConsts
    {
        SRC_Null,
        SRC_Void,
        SRC_EmptyExcSet,

        SRC_NumSpecialRefConsts
    };

    // The "values" of special ref consts will be all be "null" -- their differing meanings will
    // be carried by the distinct value numbers.
    static class Object* s_specialRefConsts[SRC_NumSpecialRefConsts];
    static class Object* s_nullConst;

#ifdef DEBUG
    // This helps test some performance pathologies related to "evaluation" of VNF_MapSelect terms,
    // especially relating to GcHeap/ByrefExposed.  We count the number of applications of such terms we consider,
    // and if this exceeds a limit, indicated by a DOTNET_ variable, we assert.
    unsigned m_numMapSels;
#endif
};

template <>
struct ValueNumStore::VarTypConv<TYP_INT>
{
    typedef INT32 Type;
    typedef int   Lang;
};
template <>
struct ValueNumStore::VarTypConv<TYP_FLOAT>
{
    typedef INT32 Type;
    typedef float Lang;
};
template <>
struct ValueNumStore::VarTypConv<TYP_LONG>
{
    typedef INT64 Type;
    typedef INT64 Lang;
};
template <>
struct ValueNumStore::VarTypConv<TYP_DOUBLE>
{
    typedef INT64  Type;
    typedef double Lang;
};

#if defined(FEATURE_SIMD)
template <>
struct ValueNumStore::VarTypConv<TYP_SIMD8>
{
    typedef simd8_t Type;
    typedef simd8_t Lang;
};
template <>
struct ValueNumStore::VarTypConv<TYP_SIMD12>
{
    typedef simd12_t Type;
    typedef simd12_t Lang;
};
template <>
struct ValueNumStore::VarTypConv<TYP_SIMD16>
{
    typedef simd16_t Type;
    typedef simd16_t Lang;
};
#if defined(TARGET_XARCH)
template <>
struct ValueNumStore::VarTypConv<TYP_SIMD32>
{
    typedef simd32_t Type;
    typedef simd32_t Lang;
};

template <>
struct ValueNumStore::VarTypConv<TYP_SIMD64>
{
    typedef simd64_t Type;
    typedef simd64_t Lang;
};
#endif // TARGET_XARCH

#if defined(FEATURE_MASKED_HW_INTRINSICS)
template <>
struct ValueNumStore::VarTypConv<TYP_MASK>
{
    typedef simdmask_t Type;
    typedef simdmask_t Lang;
};
#endif // FEATURE_MASKED_HW_INTRINSICS
#endif // FEATURE_SIMD

template <>
struct ValueNumStore::VarTypConv<TYP_BYREF>
{
    typedef size_t Type;
    typedef void*  Lang;
};
template <>
struct ValueNumStore::VarTypConv<TYP_REF>
{
    typedef class Object* Type;
    typedef class Object* Lang;
};

// Get the actual value and coerce the actual type c->m_typ to the wanted type T.
template <typename T>
FORCEINLINE T ValueNumStore::SafeGetConstantValue(Chunk* c, unsigned offset)
{
    switch (c->m_typ)
    {
        case TYP_REF:
            return CoerceTypRefToT<T>(c, offset);
        case TYP_BYREF:
            return static_cast<T>(reinterpret_cast<VarTypConv<TYP_BYREF>::Type*>(c->m_defs)[offset]);
        case TYP_INT:
            return static_cast<T>(reinterpret_cast<VarTypConv<TYP_INT>::Type*>(c->m_defs)[offset]);
        case TYP_LONG:
            return static_cast<T>(reinterpret_cast<VarTypConv<TYP_LONG>::Type*>(c->m_defs)[offset]);
        case TYP_FLOAT:
            return static_cast<T>(reinterpret_cast<VarTypConv<TYP_FLOAT>::Lang*>(c->m_defs)[offset]);
        case TYP_DOUBLE:
            return static_cast<T>(reinterpret_cast<VarTypConv<TYP_DOUBLE>::Lang*>(c->m_defs)[offset]);
        default:
            assert(false);
            return (T)0;
    }
}

#if defined(FEATURE_SIMD)
template <>
FORCEINLINE simd8_t ValueNumStore::SafeGetConstantValue<simd8_t>(Chunk* c, unsigned offset)
{
    assert(c->m_typ == TYP_SIMD8);
    return reinterpret_cast<VarTypConv<TYP_SIMD8>::Lang*>(c->m_defs)[offset];
}

template <>
FORCEINLINE simd12_t ValueNumStore::SafeGetConstantValue<simd12_t>(Chunk* c, unsigned offset)
{
    assert(c->m_typ == TYP_SIMD12);
    return reinterpret_cast<VarTypConv<TYP_SIMD12>::Lang*>(c->m_defs)[offset];
}

template <>
FORCEINLINE simd16_t ValueNumStore::SafeGetConstantValue<simd16_t>(Chunk* c, unsigned offset)
{
    assert(c->m_typ == TYP_SIMD16);
    return reinterpret_cast<VarTypConv<TYP_SIMD16>::Lang*>(c->m_defs)[offset];
}

#if defined(TARGET_XARCH)
template <>
FORCEINLINE simd32_t ValueNumStore::SafeGetConstantValue<simd32_t>(Chunk* c, unsigned offset)
{
    assert(c->m_typ == TYP_SIMD32);
    return reinterpret_cast<VarTypConv<TYP_SIMD32>::Lang*>(c->m_defs)[offset];
}

template <>
FORCEINLINE simd64_t ValueNumStore::SafeGetConstantValue<simd64_t>(Chunk* c, unsigned offset)
{
    assert(c->m_typ == TYP_SIMD64);
    return reinterpret_cast<VarTypConv<TYP_SIMD64>::Lang*>(c->m_defs)[offset];
}
#endif // TARGET_XARCH

#if defined(FEATURE_MASKED_HW_INTRINSICS)
template <>
FORCEINLINE simdmask_t ValueNumStore::SafeGetConstantValue<simdmask_t>(Chunk* c, unsigned offset)
{
    assert(c->m_typ == TYP_MASK);
    return reinterpret_cast<VarTypConv<TYP_MASK>::Lang*>(c->m_defs)[offset];
}
#endif // FEATURE_MASKED_HW_INTRINSICS

template <>
FORCEINLINE simd8_t ValueNumStore::ConstantValueInternal<simd8_t>(ValueNum vn DEBUGARG(bool coerce))
{
    Chunk* c = m_chunks.GetNoExpand(GetChunkNum(vn));
    assert(c->m_attribs == CEA_Const);

    unsigned offset = ChunkOffset(vn);

    assert(c->m_typ == TYP_SIMD8);
    assert(!coerce);

    return SafeGetConstantValue<simd8_t>(c, offset);
}

template <>
FORCEINLINE simd12_t ValueNumStore::ConstantValueInternal<simd12_t>(ValueNum vn DEBUGARG(bool coerce))
{
    Chunk* c = m_chunks.GetNoExpand(GetChunkNum(vn));
    assert(c->m_attribs == CEA_Const);

    unsigned offset = ChunkOffset(vn);

    assert(c->m_typ == TYP_SIMD12);
    assert(!coerce);

    return SafeGetConstantValue<simd12_t>(c, offset);
}

template <>
FORCEINLINE simd16_t ValueNumStore::ConstantValueInternal<simd16_t>(ValueNum vn DEBUGARG(bool coerce))
{
    Chunk* c = m_chunks.GetNoExpand(GetChunkNum(vn));
    assert(c->m_attribs == CEA_Const);

    unsigned offset = ChunkOffset(vn);

    assert(c->m_typ == TYP_SIMD16);
    assert(!coerce);

    return SafeGetConstantValue<simd16_t>(c, offset);
}

#if defined(TARGET_XARCH)
template <>
FORCEINLINE simd32_t ValueNumStore::ConstantValueInternal<simd32_t>(ValueNum vn DEBUGARG(bool coerce))
{
    Chunk* c = m_chunks.GetNoExpand(GetChunkNum(vn));
    assert(c->m_attribs == CEA_Const);

    unsigned offset = ChunkOffset(vn);

    assert(c->m_typ == TYP_SIMD32);
    assert(!coerce);

    return SafeGetConstantValue<simd32_t>(c, offset);
}

template <>
FORCEINLINE simd64_t ValueNumStore::ConstantValueInternal<simd64_t>(ValueNum vn DEBUGARG(bool coerce))
{
    Chunk* c = m_chunks.GetNoExpand(GetChunkNum(vn));
    assert(c->m_attribs == CEA_Const);

    unsigned offset = ChunkOffset(vn);

    assert(c->m_typ == TYP_SIMD64);
    assert(!coerce);

    return SafeGetConstantValue<simd64_t>(c, offset);
}
#endif // TARGET_XARCH

#if defined(FEATURE_MASKED_HW_INTRINSICS)
template <>
FORCEINLINE simdmask_t ValueNumStore::ConstantValueInternal<simdmask_t>(ValueNum vn DEBUGARG(bool coerce))
{
    Chunk* c = m_chunks.GetNoExpand(GetChunkNum(vn));
    assert(c->m_attribs == CEA_Const);

    unsigned offset = ChunkOffset(vn);

    assert(c->m_typ == TYP_MASK);
    assert(!coerce);

    return SafeGetConstantValue<simdmask_t>(c, offset);
}
#endif // FEATURE_MASKED_HW_INTRINSICS
#endif // FEATURE_SIMD

// Inline functions.

// static
inline bool ValueNumStore::GenTreeOpIsLegalVNFunc(genTreeOps gtOper)
{
    return (s_vnfOpAttribs[gtOper] & VNFOA_IllegalGenTreeOp) == 0;
}

// static
inline bool ValueNumStore::VNFuncIsCommutative(VNFunc vnf)
{
    return (s_vnfOpAttribs[vnf] & VNFOA_Commutative) != 0;
}

inline bool ValueNumStore::VNFuncIsComparison(VNFunc vnf)
{
    if (vnf >= VNF_Boundary)
    {
        // For integer types we have unsigned comparisons, and
        // for floating point types these are the unordered variants.
        //
        return ((vnf == VNF_LT_UN) || (vnf == VNF_LE_UN) || (vnf == VNF_GE_UN) || (vnf == VNF_GT_UN));
    }
    genTreeOps gtOp = genTreeOps(vnf);
    return GenTree::OperIsCompare(gtOp) != 0;
}

inline bool ValueNumStore::VNFuncIsSignedComparison(VNFunc vnf)
{
    if (vnf >= VNF_Boundary)
    {
        return false;
    }
    return GenTree::OperIsCompare(genTreeOps(vnf)) != 0;
}

template <>
inline size_t ValueNumStore::CoerceTypRefToT(Chunk* c, unsigned offset)
{
    return reinterpret_cast<size_t>(reinterpret_cast<VarTypConv<TYP_REF>::Type*>(c->m_defs)[offset]);
}

template <typename T>
inline T ValueNumStore::CoerceTypRefToT(Chunk* c, unsigned offset)
{
    noway_assert(sizeof(T) >= sizeof(VarTypConv<TYP_REF>::Type));
    unreached();
}

/*****************************************************************************/
#endif // _VALUENUM_H_
/*****************************************************************************/
