/// String orderings the spec pins down. Swift's own `String <` compares by Unicode canonical
/// equivalence, which is neither of these, so the rules never use it.
public enum StringOrder {
    /// Byte-wise comparison of the UTF-8 encodings (equal to code point order). Used for sorted
    /// ID lists, sorted records and value-order ties (spec/sync-protocol.md section 4.2).
    public static func utf8Less(_ a: String, _ b: String) -> Bool {
        a.utf8.lexicographicallyPrecedes(b.utf8)
    }

    /// Three-way UTF-8 byte comparison: -1, 0 or 1.
    public static func utf8Compare(_ a: String, _ b: String) -> Int {
        if a.utf8.elementsEqual(b.utf8) { return 0 }
        return utf8Less(a, b) ? -1 : 1
    }

    /// .NET `StringComparer.OrdinalIgnoreCase`: upper-case both strings code unit by code unit
    /// (simple, culture-invariant mapping), then compare UTF-16 code units. "By name" in the core
    /// rules always means this (spec/conformance/core/README.md, "String ordering").
    public static func ordinalIgnoreCaseCompare(_ a: String, _ b: String) -> Int {
        var ia = a.utf16.makeIterator(), ib = b.utf16.makeIterator()
        while true {
            switch (ia.next(), ib.next()) {
            case (nil, nil): return 0
            case (nil, _): return -1
            case (_, nil): return 1
            case let (ca?, cb?):
                let ua = upper(ca), ub = upper(cb)
                if ua != ub { return ua < ub ? -1 : 1 }
            }
        }
    }

    public static func ordinalIgnoreCaseLess(_ a: String, _ b: String) -> Bool {
        ordinalIgnoreCaseCompare(a, b) < 0
    }

    private static func upper(_ unit: UInt16) -> UInt16 {
        if unit >= 0x61, unit <= 0x7A { return unit - 0x20 }
        if unit < 0x80 || UTF16.isLeadSurrogate(unit) || UTF16.isTrailSurrogate(unit) { return unit }
        guard let scalar = Unicode.Scalar(unit) else { return unit }
        let mapped = scalar.properties.uppercaseMapping.unicodeScalars
        // Only one-to-one mappings within the BMP, as .NET's invariant simple case mapping.
        guard mapped.count == 1, let m = mapped.first, m.value <= 0xFFFF else { return unit }
        return UInt16(m.value)
    }
}

extension Array {
    /// A stable sort (Swift's `sort` is not guaranteed stable): equal elements keep their order.
    func stableSorted(by less: (Element, Element) -> Bool) -> [Element] {
        enumerated()
            .sorted { a, b in less(a.element, b.element) || (!less(b.element, a.element) && a.offset < b.offset) }
            .map(\.element)
    }
}
