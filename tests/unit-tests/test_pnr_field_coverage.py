"""Every masked field of the PNR field list has a recognizer in pnr.xml, or an approved-exception category.

The field list is the PNR section of the removed field policy (feed-policies.xml): every field with a Mask policy is listed
below. Keep fields (dates and times, the record locator, segments, phoneCity, MileageText, MileageQuoteText, FareText and
the like) stay unchanged and are not listed. Value-free: the table holds field names, recognizer names and categories.
"""
import json
import unittest
import xml.etree.ElementTree as ET

from decoded_text_support import FIXTURES, RECOGNIZERS

# The approved-exception categories, by the start of their reason in parity-exceptions.json.
CATEGORIES = {
    "free text": "free-text remark",
    "short string": "binary string shorter than 4 characters",
    "phone marker": "phone remark:",
    "bare phone": "phone number in a string of its own",
    "zero identifier": "change identifier without a letter A-F",
    "received-from code": "received-from code outside",
}

# Masked field -> (recognizers that cover it, approved-exception categories for what they leave).
FIELDS = {
    "/p2CreationTime": (["creationTime"], []),
    "/originChangeIdentifier": (["changeIdentifier"], []),
    "/originIdentifier": (["originIdentifier"], []),
    "/surname": (["passengerName", "loneNamePart"], []),
    "/givenName": (["passengerName", "loneNamePart"], []),
    "/factText/IssuingCountry": (["docsIssuingCountry"], []),
    "/factText/DocumentNumber": (["docsDocumentNumber"], []),
    "/factText/Nationality": (["docsNationality", "osiNationality"], []),
    "/factText/DateOfBirth": (["dateOfBirth"], []),
    "/factText/Gender": (["gender"], []),
    "/factText/DocumentExpiry": (["docsExpiry"], []),
    "/factText/PassengerName": (["passengerName"], []),
    "/factText/AssociatedName": (["passengerName"], []),
    "/factText/EmailAddress": (["ctceEmail"], []),
    "/factText/PhoneNumber": (["ctcmPhone"], []),
    "/factText/TicketNumber": (["ticketNumber"], []),
    "/factText/Text": (["osiNationality"], ["free text"]),
    "/frequentFlyerAccount/Account": (["loyaltyNumber"], []),
    "/frequentFlyerAccount/Status": (["loyaltyStatus"], []),
    "/frequentFlyerAccount/Text": (["loyaltyNumber"], []),
    "/frequentFlyerHolderName": (["passengerName"], []),
    "/phoneAreaCode": (["phoneRemark"], ["short string"]),
    "/phoneText/PhoneRemark": ([], ["phone marker"]),
    "/phoneText/Text": (["phoneRemark"], ["bare phone"]),
    "/remarkText/CardSuffix": (["cardSuffix"], []),
    "/remarkText/IpAddress": (["ipAddress"], []),
    "/remarkText/MemberName": (["memberName"], []),
    "/remarkText/MemberAccount": (["loyaltyNumber"], []),
    "/remarkText/MemberStatus": (["loyaltyStatus"], []),
    "/remarkText/SponsorAccount": (["loyaltyNumber"], []),
    "/remarkText/Balance": (["accountBalance"], []),
    "/remarkText/MsoIdentifier": (["msoId"], []),
    "/remarkText/RequestedEmail": (["requestedEmail"], []),
    "/remarkText/FareQuoteReference": (["orderId"], []),
    "/remarkText/TicketOrderReference": (["orderId"], []),
    "/remarkText/AddressCity": (["address"], []),
    "/remarkText/AddressState": (["address"], []),
    "/remarkText/AddressPostalCode": (["address"], []),
    "/remarkText/AddressText": (["address"], []),
    "/remarkText/RviReference": (["rviId"], []),
    "/remarkText/ApprovalReference": (["approvalCode"], []),
    "/remarkText/MileageOrderReference": (["orderId"], []),
    "/remarkText/Text": ([], ["free text"]),
    "/fareReferenceText/LoyaltyAccount": (["loyaltyNumber"], []),
    "/fareReferenceText/TicketReference": (["ticketNumber"], []),
    "/fareReferenceText/Text": ([], ["free text"]),
    "/receivedFromCode": (["changeIdentifier"], ["received-from code"]),
    "/receivedFromText": (["originIdentifier"], ["free text"]),
    "/remarkIndexText/LoyaltyAccount": (["loyaltyNumber"], []),
    "/remarkIndexText/Text": ([], ["free text"]),
    "/etktPassengerName": (["passengerName"], []),
    "/historyChangeIdentifier": (["changeIdentifier"], ["zero identifier"]),
    "/passengerHistoryText": ([], ["free text"]),
}


class PnrFieldCoverageTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        root = ET.parse(RECOGNIZERS / "pnr.xml").getroot()
        cls.recognizers = {r.get("name") for r in root.iter("recognizer")
                           if r.get("type", "custom") in ("custom", "lone_name") and r.get("enabled", "true") == "true"}
        cls.reasons = {e["reason"] for e in json.loads((FIXTURES / "parity-exceptions.json").read_text(encoding="utf-8"))}

    def test_the_table_lists_every_masked_field_of_the_pnr_field_list(self):
        self.assertEqual(len(FIELDS), 53)

    def test_every_masked_field_has_a_recognizer_or_an_exception_category(self):
        for field, (recognizers, categories) in FIELDS.items():
            with self.subTest(field=field):
                self.assertTrue(recognizers or categories)
                self.assertLessEqual(set(recognizers), self.recognizers, "a listed recognizer is missing from pnr.xml")
                for category in categories:
                    self.assertTrue(any(reason.startswith(CATEGORIES[category]) for reason in self.reasons),
                                    f"no approved exception of category {category!r}")

    def test_every_recognizer_and_exception_reason_is_accounted_for(self):
        used = {name for recognizers, _ in FIELDS.values() for name in recognizers}
        self.assertEqual(self.recognizers - used, set(), "a recognizer in pnr.xml covers no listed field")
        for reason in self.reasons:
            with self.subTest(reason=reason[:40]):
                self.assertTrue(any(reason.startswith(prefix) for prefix in CATEGORIES.values()), "an exception outside every category")


if __name__ == "__main__":
    unittest.main()
