// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;
using Xunit;

namespace System.Text.Json.Serialization.Tests
{
    public class PolymorphicTests_Span : PolymorphicTests
    {
        public PolymorphicTests_Span() : base(JsonSerializerWrapper.SpanSerializer) { }
    }

    public class PolymorphicTests_String : PolymorphicTests
    {
        public PolymorphicTests_String() : base(JsonSerializerWrapper.StringSerializer) { }
    }

    public class PolymorphicTests_AsyncStream : PolymorphicTests
    {
        public PolymorphicTests_AsyncStream() : base(JsonSerializerWrapper.AsyncStreamSerializer) { }
    }

    public class PolymorphicTests_AsyncStreamWithSmallBuffer : PolymorphicTests
    {
        public PolymorphicTests_AsyncStreamWithSmallBuffer() : base(JsonSerializerWrapper.AsyncStreamSerializerWithSmallBuffer) { }
    }

    public class PolymorphicTests_SyncStream : PolymorphicTests
    {
        public PolymorphicTests_SyncStream() : base(JsonSerializerWrapper.SyncStreamSerializer) { }
    }

    public class PolymorphicTests_Writer : PolymorphicTests
    {
        public PolymorphicTests_Writer() : base(JsonSerializerWrapper.ReaderWriterSerializer) { }
    }

    public class PolymorphicTests_Document : PolymorphicTests
    {
        public PolymorphicTests_Document() : base(JsonSerializerWrapper.DocumentSerializer) { }
    }

    public class PolymorphicTests_Element : PolymorphicTests
    {
        public PolymorphicTests_Element() : base(JsonSerializerWrapper.ElementSerializer) { }
    }

    public class PolymorphicTests_Node : PolymorphicTests
    {
        public PolymorphicTests_Node() : base(JsonSerializerWrapper.NodeSerializer) { }
    }

    public class PolymorphicTests_Pipe : PolymorphicTests
    {
        public PolymorphicTests_Pipe() : base(JsonSerializerWrapper.AsyncPipeSerializer) { }
    }

    public class PolymorphicTests_PipeWithSmallBuffer : PolymorphicTests
    {
        public PolymorphicTests_PipeWithSmallBuffer() : base(JsonSerializerWrapper.AsyncPipeSerializerWithSmallBuffer) { }
    }

    public abstract partial class PolymorphicTests : SerializerTests
    {
        public PolymorphicTests(JsonSerializerWrapper serializer) : base(serializer)
        {
        }

        [Theory]
        [InlineData(1, "1")]
        [InlineData("stringValue", @"""stringValue""")]
        [InlineData(true, "true")]
        [InlineData(null, "null")]
        [InlineData(new int[] { 1, 2, 3}, "[1,2,3]")]
        public async Task PrimitivesAsRootObject(object? value, string expectedJson)
        {
            string json = await Serializer.SerializeWrapper(value);
            Assert.Equal(expectedJson, json);
            json = await Serializer.SerializeWrapper(value, typeof(object));
            Assert.Equal(expectedJson, json);

            var options = new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
            JsonTypeInfo<object> objectTypeInfo = (JsonTypeInfo<object>)options.GetTypeInfo(typeof(object));
            json = await Serializer.SerializeWrapper(value, objectTypeInfo);
            Assert.Equal(expectedJson, json);
        }

        [Fact]
        public void ReadPrimitivesFail()
        {
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<object>(@""));
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<object>(@"a"));
        }

        [Fact]
        public void ParseUntyped()
        {
            object obj = JsonSerializer.Deserialize<object>(@"""hello""");
            Assert.IsType<JsonElement>(obj);
            JsonElement element = (JsonElement)obj;
            Assert.Equal(JsonValueKind.String, element.ValueKind);
            Assert.Equal("hello", element.GetString());

            obj = JsonSerializer.Deserialize<object>(@"true");
            element = (JsonElement)obj;
            Assert.Equal(JsonValueKind.True, element.ValueKind);
            Assert.True(element.GetBoolean());

            obj = JsonSerializer.Deserialize<object>(@"null");
            Assert.Null(obj);

            obj = JsonSerializer.Deserialize<object>(@"[]");
            element = (JsonElement)obj;
            Assert.Equal(JsonValueKind.Array, element.ValueKind);
        }

        [Fact]
        public async Task ArrayAsRootObject()
        {
            const string ExpectedJson = @"[1,true,{""City"":""MyCity""},null,""foo""]";
            const string ReversedExpectedJson = @"[""foo"",null,{""City"":""MyCity""},true,1]";

            string[] expectedObjects = { @"""foo""", @"null", @"{""City"":""MyCity""}", @"true", @"1" };

            var address = new Address();
            address.Initialize();

            var array = new object[] { 1, true, address, null, "foo" };
            string json = await Serializer.SerializeWrapper(array);
            Assert.Equal(ExpectedJson, json);

            var dictionary = new Dictionary<string, string> { { "City", "MyCity" } };
            var arrayWithDictionary = new object[] { 1, true, dictionary, null, "foo" };
            json = await Serializer.SerializeWrapper(arrayWithDictionary);
            Assert.Equal(ExpectedJson, json);

            json = await Serializer.SerializeWrapper<object>(array);
            Assert.Equal(ExpectedJson, json);

            List<object> list = new List<object> { 1, true, address, null, "foo" };
            json = await Serializer.SerializeWrapper(list);
            Assert.Equal(ExpectedJson, json);

            json = await Serializer.SerializeWrapper<object>(list);
            Assert.Equal(ExpectedJson, json);

            IEnumerable ienumerable = new List<object> { 1, true, address, null, "foo" };
            json = await Serializer.SerializeWrapper(ienumerable);
            Assert.Equal(ExpectedJson, json);

            json = await Serializer.SerializeWrapper<object>(ienumerable);
            Assert.Equal(ExpectedJson, json);

            IList ilist = new List<object> { 1, true, address, null, "foo" };
            json = await Serializer.SerializeWrapper(ilist);
            Assert.Equal(ExpectedJson, json);

            json = await Serializer.SerializeWrapper<object>(ilist);
            Assert.Equal(ExpectedJson, json);

            ICollection icollection = new List<object> { 1, true, address, null, "foo" };
            json = await Serializer.SerializeWrapper(icollection);
            Assert.Equal(ExpectedJson, json);

            json = await Serializer.SerializeWrapper<object>(icollection);
            Assert.Equal(ExpectedJson, json);

            IEnumerable<object> genericIEnumerable = new List<object> { 1, true, address, null, "foo" };
            json = await Serializer.SerializeWrapper(genericIEnumerable);
            Assert.Equal(ExpectedJson, json);

            json = await Serializer.SerializeWrapper<object>(genericIEnumerable);
            Assert.Equal(ExpectedJson, json);

            IList<object> genericIList = new List<object> { 1, true, address, null, "foo" };
            json = await Serializer.SerializeWrapper(genericIList);
            Assert.Equal(ExpectedJson, json);

            json = await Serializer.SerializeWrapper<object>(genericIList);
            Assert.Equal(ExpectedJson, json);

            ICollection<object> genericICollection = new List<object> { 1, true, address, null, "foo" };
            json = await Serializer.SerializeWrapper(genericICollection);
            Assert.Equal(ExpectedJson, json);

            json = await Serializer.SerializeWrapper<object>(genericICollection);
            Assert.Equal(ExpectedJson, json);

            IReadOnlyCollection<object> genericIReadOnlyCollection = new List<object> { 1, true, address, null, "foo" };
            json = await Serializer.SerializeWrapper(genericIReadOnlyCollection);
            Assert.Equal(ExpectedJson, json);

            json = await Serializer.SerializeWrapper<object>(genericIReadOnlyCollection);
            Assert.Equal(ExpectedJson, json);

            IReadOnlyList<object> genericIReadonlyList = new List<object> { 1, true, address, null, "foo" };
            json = await Serializer.SerializeWrapper(genericIReadonlyList);
            Assert.Equal(ExpectedJson, json);

            json = await Serializer.SerializeWrapper<object>(genericIReadonlyList);
            Assert.Equal(ExpectedJson, json);

            ISet<object> iset = new HashSet<object> { 1, true, address, null, "foo" };
            json = await Serializer.SerializeWrapper(iset);
            Assert.Equal(ExpectedJson, json);

            json = await Serializer.SerializeWrapper<object>(iset);
            Assert.Equal(ExpectedJson, json);

            Stack<object> stack = new Stack<object>(new List<object> { 1, true, address, null, "foo" });
            json = await Serializer.SerializeWrapper(stack);
            Assert.Equal(ReversedExpectedJson, json);

            json = await Serializer.SerializeWrapper<object>(stack);
            Assert.Equal(ReversedExpectedJson, json);

            Queue<object> queue = new Queue<object>(new List<object> { 1, true, address, null, "foo" });
            json = await Serializer.SerializeWrapper(queue);
            Assert.Equal(ExpectedJson, json);

            json = await Serializer.SerializeWrapper<object>(queue);
            Assert.Equal(ExpectedJson, json);

            HashSet<object> hashset = new HashSet<object>(new List<object> { 1, true, address, null, "foo" });
            json = await Serializer.SerializeWrapper(hashset);
            Assert.Equal(ExpectedJson, json);

            json = await Serializer.SerializeWrapper<object>(hashset);
            Assert.Equal(ExpectedJson, json);

            LinkedList<object> linkedlist = new LinkedList<object>(new List<object> { 1, true, address, null, "foo" });
            json = await Serializer.SerializeWrapper(linkedlist);
            Assert.Equal(ExpectedJson, json);

            json = await Serializer.SerializeWrapper<object>(linkedlist);
            Assert.Equal(ExpectedJson, json);

            ImmutableArray<object> immutablearray = ImmutableArray.CreateRange(new List<object> { 1, true, address, null, "foo" });
            json = await Serializer.SerializeWrapper(immutablearray);
            Assert.Equal(ExpectedJson, json);

            json = await Serializer.SerializeWrapper<object>(immutablearray);
            Assert.Equal(ExpectedJson, json);

            IImmutableList<object> iimmutablelist = ImmutableList.CreateRange(new List<object> { 1, true, address, null, "foo" });
            json = await Serializer.SerializeWrapper(iimmutablelist);
            Assert.Equal(ExpectedJson, json);

            json = await Serializer.SerializeWrapper<object>(iimmutablelist);
            Assert.Equal(ExpectedJson, json);

            IImmutableStack<object> iimmutablestack = ImmutableStack.CreateRange(new List<object> { 1, true, address, null, "foo" });
            json = await Serializer.SerializeWrapper(iimmutablestack);
            Assert.Equal(ReversedExpectedJson, json);

            json = await Serializer.SerializeWrapper<object>(iimmutablestack);
            Assert.Equal(ReversedExpectedJson, json);

            IImmutableQueue<object> iimmutablequeue = ImmutableQueue.CreateRange(new List<object> { 1, true, address, null, "foo" });
            json = await Serializer.SerializeWrapper(iimmutablequeue);
            Assert.Equal(ExpectedJson, json);

            json = await Serializer.SerializeWrapper<object>(iimmutablequeue);
            Assert.Equal(ExpectedJson, json);

            IImmutableSet<object> iimmutableset = ImmutableHashSet.CreateRange(new List<object> { 1, true, address, null, "foo" });
            json = await Serializer.SerializeWrapper(iimmutableset);
            foreach (string obj in expectedObjects)
            {
                Assert.Contains(obj, json);
            }

            json = await Serializer.SerializeWrapper<object>(iimmutableset);
            foreach (string obj in expectedObjects)
            {
                Assert.Contains(obj, json);
            }

            ImmutableHashSet<object> immutablehashset = ImmutableHashSet.CreateRange(new List<object> { 1, true, address, null, "foo" });
            json = await Serializer.SerializeWrapper(immutablehashset);
            foreach (string obj in expectedObjects)
            {
                Assert.Contains(obj, json);
            }

            json = await Serializer.SerializeWrapper<object>(immutablehashset);
            foreach (string obj in expectedObjects)
            {
                Assert.Contains(obj, json);
            }

            ImmutableList<object> immutablelist = ImmutableList.CreateRange(new List<object> { 1, true, address, null, "foo" });
            json = await Serializer.SerializeWrapper(immutablelist);
            Assert.Equal(ExpectedJson, json);

            json = await Serializer.SerializeWrapper<object>(immutablelist);
            Assert.Equal(ExpectedJson, json);

            ImmutableStack<object> immutablestack = ImmutableStack.CreateRange(new List<object> { 1, true, address, null, "foo" });
            json = await Serializer.SerializeWrapper(immutablestack);
            Assert.Equal(ReversedExpectedJson, json);

            json = await Serializer.SerializeWrapper<object>(immutablestack);
            Assert.Equal(ReversedExpectedJson, json);

            ImmutableQueue<object> immutablequeue = ImmutableQueue.CreateRange(new List<object> { 1, true, address, null, "foo" });
            json = await Serializer.SerializeWrapper(immutablequeue);
            Assert.Equal(ExpectedJson, json);

            json = await Serializer.SerializeWrapper<object>(immutablequeue);
            Assert.Equal(ExpectedJson, json);
        }

        [Fact]
        public async Task SimpleTestClassAsRootObject()
        {
            // Sanity checks on test type.
            Assert.Equal(typeof(object), typeof(SimpleTestClassWithObject).GetProperty("MyInt16").PropertyType);
            Assert.Equal(typeof(object), typeof(SimpleTestClassWithObject).GetProperty("MyBooleanTrue").PropertyType);
            Assert.Equal(typeof(object), typeof(SimpleTestClassWithObject).GetProperty("MyInt16Array").PropertyType);

            var obj = new SimpleTestClassWithObject();
            obj.Initialize();

            // Verify with actual type.
            string json = await Serializer.SerializeWrapper(obj);
            Assert.Contains(@"""MyInt16"":1", json);
            Assert.Contains(@"""MyBooleanTrue"":true", json);
            Assert.Contains(@"""MyInt16Array"":[1]", json);

            // Verify with object type.
            json = await Serializer.SerializeWrapper<object>(obj);
            Assert.Contains(@"""MyInt16"":1", json);
            Assert.Contains(@"""MyBooleanTrue"":true", json);
            Assert.Contains(@"""MyInt16Array"":[1]", json);
        }

        [Fact]
        public async Task NestedObjectAsRootObject()
        {
            void Verify(string json)
            {
                Assert.Contains(@"""Address"":{""City"":""MyCity""}", json);
                Assert.Contains(@"""List"":[""Hello"",""World""]", json);
                Assert.Contains(@"""Array"":[""Hello"",""Again""]", json);
                Assert.Contains(@"""IEnumerable"":[""Hello"",""World""]", json);
                Assert.Contains(@"""IList"":[""Hello"",""World""]", json);
                Assert.Contains(@"""ICollection"":[""Hello"",""World""]", json);
                Assert.Contains(@"""IEnumerableT"":[""Hello"",""World""]", json);
                Assert.Contains(@"""IListT"":[""Hello"",""World""]", json);
                Assert.Contains(@"""ICollectionT"":[""Hello"",""World""]", json);
                Assert.Contains(@"""IReadOnlyCollectionT"":[""Hello"",""World""]", json);
                Assert.Contains(@"""IReadOnlyListT"":[""Hello"",""World""]", json);
                Assert.Contains(@"""ISetT"":[""Hello"",""World""]", json);
                Assert.Contains(@"""StackT"":[""World"",""Hello""]", json);
                Assert.Contains(@"""QueueT"":[""Hello"",""World""]", json);
                Assert.Contains(@"""HashSetT"":[""Hello"",""World""]", json);
                Assert.Contains(@"""LinkedListT"":[""Hello"",""World""]", json);
                Assert.Contains(@"""SortedSetT"":[""Hello"",""World""]", json);
                Assert.Contains(@"""ImmutableArrayT"":[""Hello"",""World""]", json);
                Assert.Contains(@"""IImmutableListT"":[""Hello"",""World""]", json);
                Assert.Contains(@"""IImmutableStackT"":[""World"",""Hello""]", json);
                Assert.Contains(@"""IImmutableQueueT"":[""Hello"",""World""]", json);
                Assert.True(json.Contains(@"""IImmutableSetT"":[""Hello"",""World""]") || json.Contains(@"""IImmutableSetT"":[""World"",""Hello""]"));
                Assert.True(json.Contains(@"""ImmutableHashSetT"":[""Hello"",""World""]") || json.Contains(@"""ImmutableHashSetT"":[""World"",""Hello""]"));
                Assert.Contains(@"""ImmutableListT"":[""Hello"",""World""]", json);
                Assert.Contains(@"""ImmutableStackT"":[""World"",""Hello""]", json);
                Assert.Contains(@"""ImmutableQueueT"":[""Hello"",""World""]", json);
                Assert.Contains(@"""ImmutableSortedSetT"":[""Hello"",""World""]", json);
                Assert.Contains(@"""NullableInt"":42", json);
                Assert.Contains(@"""Object"":{}", json);
                Assert.Contains(@"""NullableIntArray"":[null,42,null]", json);
            }

            // Sanity checks on test type.
            Assert.Equal(typeof(object), typeof(ObjectWithObjectProperties).GetProperty("Address").PropertyType);
            Assert.Equal(typeof(object), typeof(ObjectWithObjectProperties).GetProperty("List").PropertyType);
            Assert.Equal(typeof(object), typeof(ObjectWithObjectProperties).GetProperty("Array").PropertyType);
            Assert.Equal(typeof(object), typeof(ObjectWithObjectProperties).GetProperty("NullableInt").PropertyType);
            Assert.Equal(typeof(object), typeof(ObjectWithObjectProperties).GetProperty("NullableIntArray").PropertyType);

            var obj = new ObjectWithObjectProperties();

            string json = await Serializer.SerializeWrapper(obj);
            Verify(json);

            json = await Serializer.SerializeWrapper<object>(obj);
            Verify(json);
        }

        [Fact]
        public async Task NestedObjectAsRootObjectIgnoreNullable()
        {
            // Ensure that null properties are properly written and support ignore.
            var obj = new ObjectWithObjectProperties();
            obj.NullableInt = null;

            string json = await Serializer.SerializeWrapper(obj);
            Assert.Contains(@"""NullableInt"":null", json);

            JsonSerializerOptions options = new JsonSerializerOptions();
            options.IgnoreNullValues = true;
            json = await Serializer.SerializeWrapper(obj, options);
            Assert.DoesNotContain(@"""NullableInt"":null", json);
        }

        [Fact]
        public async Task StaticAnalysisBaseline()
        {
            Customer customer = new Customer();
            customer.Initialize();
            customer.Verify();

            string json = await Serializer.SerializeWrapper(customer);
            Customer deserializedCustomer = JsonSerializer.Deserialize<Customer>(json);
            deserializedCustomer.Verify();
        }

        [Fact]
        public async Task StaticAnalysis()
        {
            Customer customer = new Customer();
            customer.Initialize();
            customer.Verify();

            Person person = customer;

            // Generic inference used <TValue> = <Person>
            string json = await Serializer.SerializeWrapper(person);

            Customer deserializedCustomer = JsonSerializer.Deserialize<Customer>(json);

            // We only serialized the Person base class, so the Customer fields should be default.
            Assert.Equal(typeof(Customer), deserializedCustomer.GetType());
            Assert.Equal(0, deserializedCustomer.CreditLimit);
            ((Person)deserializedCustomer).VerifyNonVirtual();
        }

        [Fact]
        public async Task WriteStringWithRuntimeType()
        {
            Customer customer = new Customer();
            customer.Initialize();
            customer.Verify();

            Person person = customer;

            string json = await Serializer.SerializeWrapper(person, person.GetType());

            Customer deserializedCustomer = JsonSerializer.Deserialize<Customer>(json);

            // We serialized the Customer
            Assert.Equal(typeof(Customer), deserializedCustomer.GetType());
            deserializedCustomer.Verify();
        }

        [Fact]
        public async Task StaticAnalysisWithRelationship()
        {
            UsaCustomer usaCustomer = new UsaCustomer();
            usaCustomer.Initialize();
            usaCustomer.Verify();

            // Note: this could be typeof(UsaAddress) if we preserve objects created in the ctor. Currently we only preserve IEnumerables.
            Assert.Equal(typeof(Address), usaCustomer.Address.GetType());

            Customer customer = usaCustomer;

            // Generic inference used <TValue> = <Customer>
            string json = await Serializer.SerializeWrapper(customer);

            UsaCustomer deserializedCustomer = JsonSerializer.Deserialize<UsaCustomer>(json);

            // We only serialized the Customer base class
            Assert.Equal(typeof(UsaCustomer), deserializedCustomer.GetType());
            Assert.Equal(typeof(Address), deserializedCustomer.Address.GetType());
            ((Customer)deserializedCustomer).VerifyNonVirtual();
        }

        [Fact]
        public void PolymorphicInterface_NotSupported()
        {
            Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize<MyClass>(@"{ ""Value"": ""A value"", ""Thing"": { ""Number"": 123 } }"));
        }

        [Fact]
        public void GenericListOfInterface_WithInvalidJson_ThrowsJsonException()
        {
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<MyThingCollection>("false"));
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<MyThingCollection>("{}"));
        }

        [Fact]
        public void GenericListOfInterface_WithValidJson_ThrowsNotSupportedException()
        {
            Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize<MyThingCollection>("[{}]"));
        }

        [Fact]
        public void GenericDictionaryOfInterface_WithInvalidJson_ThrowsJsonException()
        {
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<MyThingDictionary>(@"{"""":1}"));
        }

        [Fact]
        public void GenericDictionaryOfInterface_WithValidJson_ThrowsNotSupportedException()
        {
            Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize<MyThingDictionary>(@"{"""":{}}"));
        }

        [Fact]
        public async Task AnonymousType()
        {
            const string Expected = @"{""x"":1,""y"":true}";
            var value = new { x = 1, y = true };

            // Strongly-typed.
            string json = await Serializer.SerializeWrapper(value);
            Assert.Equal(Expected, json);

            // Boxed.
            object objValue = value;
            json = await Serializer.SerializeWrapper(objValue);
            Assert.Equal(Expected, json);
        }

        [Fact]
        public async Task CustomResolverWithFailingAncestorType_DoesNotSurfaceException()
        {
            var options = new JsonSerializerOptions
            {
                TypeInfoResolver = new DefaultJsonTypeInfoResolver
                {
                    Modifiers =
                    {
                        static typeInfo =>
                        {
                            if (typeInfo.Type == typeof(MyThing) ||
                                typeInfo.Type == typeof(IList))
                            {
                                throw new InvalidOperationException("some latent custom resolution bug");
                            }
                        }
                    }
                }
            };

            object value = new MyDerivedThing { Number = 42 };
            string json = await Serializer.SerializeWrapper(value, options);
            Assert.Equal("""{"Number":42}""", json);

            value = new int[] { 1, 2, 3 };
            json = await Serializer.SerializeWrapper(value, options);
            Assert.Equal("[1,2,3]", json);
        }

        class MyClass
        {
            public string Value { get; set; }
            public IThing Thing { get; set; }
        }

        interface IThing
        {
            int Number { get; set; }
        }

        class MyThing : IThing
        {
            public int Number { get; set; }
        }

        class MyDerivedThing : MyThing
        {
        }

        class MyThingCollection : List<IThing> { }

        class MyThingDictionary : Dictionary<string, IThing> { }

        [Theory]
        [InlineData(typeof(PolymorphicTypeWithConflictingPropertyNameAtBase), typeof(PolymorphicTypeWithConflictingPropertyNameAtBase.Derived))]
        [InlineData(typeof(PolymorphicTypeWithConflictingPropertyNameAtDerived), typeof(PolymorphicTypeWithConflictingPropertyNameAtDerived.Derived))]
        public async Task PolymorphicTypesWithConflictingPropertyNames_ThrowsInvalidOperationException(Type baseType, Type derivedType)
        {
            InvalidOperationException ex;
            object value = Activator.CreateInstance(derivedType);

            ex = Assert.Throws<InvalidOperationException>(() => Serializer.GetTypeInfo(baseType));
            ValidateException(ex);

            ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Serializer.SerializeWrapper(value, baseType));
            ValidateException(ex);

            ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Serializer.DeserializeWrapper("{}", baseType));
            ValidateException(ex);

            void ValidateException(InvalidOperationException ex)
            {
                Assert.Contains($"The type '{derivedType}' contains property 'Type' that conflicts with an existing metadata property name.", ex.Message);
            }
        }

        [JsonPolymorphic(TypeDiscriminatorPropertyName = "Type")]
        [JsonDerivedType(typeof(Derived), nameof(Derived))]
        public abstract class PolymorphicTypeWithConflictingPropertyNameAtBase
        {
            public string Type { get; set; }

            public class Derived : PolymorphicTypeWithConflictingPropertyNameAtBase
            {
                public string Name { get; set; }
            }
        }

        [JsonPolymorphic(TypeDiscriminatorPropertyName = "Type")]
        [JsonDerivedType(typeof(Derived), nameof(Derived))]
        public abstract class PolymorphicTypeWithConflictingPropertyNameAtDerived
        {
            public class Derived : PolymorphicTypeWithConflictingPropertyNameAtDerived
            {
                public string Type { get; set; }
            }
        }

        [Fact]
        public async Task PolymorphicTypeWithIgnoredConflictingPropertyName_Supported()
        {
            PolymorphicTypeWithIgnoredConflictingPropertyName value = new PolymorphicTypeWithIgnoredConflictingPropertyName.Derived();

            JsonTypeInfo typeInfo = Serializer.GetTypeInfo(typeof(PolymorphicTypeWithIgnoredConflictingPropertyName));
            Assert.NotNull(typeInfo);

            string json = await Serializer.SerializeWrapper(value);
            Assert.Equal("""{"Type":"Derived"}""", json);

            value = await Serializer.DeserializeWrapper<PolymorphicTypeWithIgnoredConflictingPropertyName>(json);
            Assert.IsType<PolymorphicTypeWithIgnoredConflictingPropertyName.Derived>(value);
        }

        [JsonPolymorphic(TypeDiscriminatorPropertyName = "Type")]
        [JsonDerivedType(typeof(Derived), nameof(Derived))]
        public abstract class PolymorphicTypeWithIgnoredConflictingPropertyName
        {
            [JsonIgnore]
            public string Type { get; set; }

            public class Derived : PolymorphicTypeWithIgnoredConflictingPropertyName;
        }

        [Fact]
        public async Task PolymorphicTypeWithExtensionDataConflictingPropertyName_Supported()
        {
            PolymorphicTypeWithExtensionDataConflictingPropertyName value = new PolymorphicTypeWithExtensionDataConflictingPropertyName.Derived();

            JsonTypeInfo typeInfo = Serializer.GetTypeInfo(typeof(PolymorphicTypeWithIgnoredConflictingPropertyName));
            Assert.NotNull(typeInfo);

            string json = await Serializer.SerializeWrapper(value);
            Assert.Equal("""{"Type":"Derived"}""", json);

            value = await Serializer.DeserializeWrapper<PolymorphicTypeWithExtensionDataConflictingPropertyName>("""{"Type":"Derived","extraProp":null}""");
            Assert.IsType<PolymorphicTypeWithExtensionDataConflictingPropertyName.Derived>(value);
            Assert.Equal(1, value.Type.Count);
            Assert.Contains("extraProp", value.Type);
        }

        [JsonPolymorphic(TypeDiscriminatorPropertyName = "Type")]
        [JsonDerivedType(typeof(Derived), nameof(Derived))]
        public abstract class PolymorphicTypeWithExtensionDataConflictingPropertyName
        {
            [JsonExtensionData]
            public JsonObject Type { get; set; }

            public class Derived : PolymorphicTypeWithExtensionDataConflictingPropertyName;
        }
    }
}
