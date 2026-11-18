// DTO types mirroring the backend JSON contract (ShortLink.Api / Core).
export interface UserResponse {
  id: string
  username: string
  email: string
  role: string
}

export interface AuthResponse {
  accessToken: string
  refreshToken: string
  expiresAt: string
  userId: string
  username: string
  email: string
  role: string
}

export interface LinkResponse {
  id: string
  slug: string
  url: string
  ownerId: string
  createdAt: string
  expiresAt: string | null
  clickCount: number
}

export interface DeviceStat {
  device: string
  count: number
}

export interface CountryStat {
  country: string
  count: number
}

export interface DayStat {
  day: string
  count: number
}

export interface AnalyticsSummary {
  linkId: string
  totalClicks: number
  byDevice: DeviceStat[]
  byCountry: CountryStat[]
  byDay: DayStat[]
}
