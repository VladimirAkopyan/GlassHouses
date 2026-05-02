"""Seed the MongoDB complaints collection with realistic test data."""
from pymongo import MongoClient
from datetime import datetime, timezone, timedelta

client = MongoClient("mongodb+srv://chatbot:chatbot@cluster0.zjwaeg.mongodb.net/?appName=Cluster0")
collection = client["housing_db"]["complaints"]

now = datetime.now(timezone.utc)

# ---------------------------------------------------------------------------
# Original GMV buildings (302 / 303 / 304) — already inserted previously.
# Uncomment and run only if you need to re-seed them from scratch.
# ---------------------------------------------------------------------------
# gmv_complaints = [ ... ]

# ---------------------------------------------------------------------------
# New batch – 21 Brune Street, E1 7ND (building_id "11")
# ---------------------------------------------------------------------------
new_complaints = [
    {
        "building_id": "11",
        "building_name": "21 Brune Street, E1 7ND",
        "category": "heating",
        "description": "Heating doesn't work in whole flat",
        "created_at": datetime(2026, 5, 2, 14, 12, 35, 313000, tzinfo=timezone.utc),
    },
    {
        "building_id": "11",
        "building_name": "21 Brune Street, E1 7ND",
        "category": "hot water",
        "description": "No hot water in the kitchen or bathroom since Monday morning. Cold showers every day.",
        "created_at": now - timedelta(days=5),
    },
    {
        "building_id": "11",
        "building_name": "21 Brune Street, E1 7ND",
        "category": "damp",
        "description": "Black mould growing on the bedroom wall behind the wardrobe. It has spread considerably over the past month.",
        "created_at": now - timedelta(days=18),
    },
    {
        "building_id": "11",
        "building_name": "21 Brune Street, E1 7ND",
        "category": "noise",
        "description": "Neighbour in flat 3B playing bass-heavy music from 11pm to 3am every weekend. Sleep is impossible.",
        "created_at": now - timedelta(days=8),
    },
    {
        "building_id": "11",
        "building_name": "21 Brune Street, E1 7ND",
        "category": "plumbing",
        "description": "Kitchen sink draining very slowly and backing up when the dishwasher runs. Possible blockage in shared stack.",
        "created_at": now - timedelta(days=3),
    },
    {
        "building_id": "11",
        "building_name": "21 Brune Street, E1 7ND",
        "category": "windows",
        "description": "Double glazing on the living room window is broken — condensation between the panes and a cold draught.",
        "created_at": now - timedelta(days=11),
    },
    {
        "building_id": "11",
        "building_name": "21 Brune Street, E1 7ND",
        "category": "electrical",
        "description": "Bathroom light fitting keeps tripping the circuit breaker. Electrician needed urgently as the fault is getting worse.",
        "created_at": now - timedelta(days=2),
    },
    {
        "building_id": "11",
        "building_name": "21 Brune Street, E1 7ND",
        "category": "communal",
        "description": "Rubbish not collected from the bin store for two weeks. Overflowing bags attracting foxes and creating a health hazard.",
        "created_at": now - timedelta(days=13),
    },
    {
        "building_id": "11",
        "building_name": "21 Brune Street, E1 7ND",
        "category": "security",
        "description": "Front door intercom panel is broken — residents cannot buzz guests in remotely and the door sticks shut.",
        "created_at": now - timedelta(days=6),
    },
    {
        "building_id": "11",
        "building_name": "21 Brune Street, E1 7ND",
        "category": "pest",
        "description": "Cockroaches found in the kitchen cupboards and under the oven. Likely entering through a gap around the pipework.",
        "created_at": now - timedelta(hours=18),
    },
]

result = collection.insert_many(new_complaints)
print(f"Inserted {len(result.inserted_ids)} complaints:")
for i, _id in enumerate(result.inserted_ids):
    c = new_complaints[i]
    print(f"  [{c['building_id']}] {c['category']:<12} — {_id}")
