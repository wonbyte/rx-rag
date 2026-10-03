namespace RxRag.Core.OpenFda;

// The openFDA label fields we keep, in the order we show them.
//
// Why a fixed list (an "allow list"): a label has 100+ fields. Many are
// noise for patients ("spl_product_data_elements", "package_label...").
// Indexing them fills search results with junk. We pick the fields that
// answer patient questions: uses, dose, warnings, interactions.
//
// Key = openFDA field name. Title = text we show in citations.
internal static class OpenFdaSections
{
    internal static readonly (string Key, string Title)[] Known =
    [
        ("boxed_warning", "Boxed warning"),
        ("indications_and_usage", "Uses"),
        ("purpose", "Purpose"),
        ("active_ingredient", "Active ingredient"),
        ("dosage_and_administration", "Directions and dosage"),
        ("contraindications", "Contraindications"),
        ("warnings_and_cautions", "Warnings and precautions"),
        ("warnings", "Warnings"),
        ("do_not_use", "Do not use"),
        ("ask_doctor", "Ask a doctor before use"),
        ("ask_doctor_or_pharmacist", "Ask a doctor or pharmacist before use"),
        ("when_using", "When using this product"),
        ("stop_use", "Stop use and ask a doctor"),
        ("pregnancy_or_breast_feeding", "Pregnancy or breast-feeding"),
        ("drug_interactions", "Drug interactions"),
        ("adverse_reactions", "Adverse reactions"),
        ("overdosage", "Overdosage"),
        ("keep_out_of_reach_of_children", "Keep out of reach of children"),
    ];
}