from pydantic import BaseModel, Field


class Complaint(BaseModel):
    """Structured complaint payload extracted from user text."""

    building_id: str = Field(description="Unique building identifier, e.g. GMV-302")
    building_name: str = Field(description="Human-friendly building name")
    category: str = Field(description="Issue type, e.g. plumbing, noise, heating")
    description: str = Field(description="Detailed complaint description")

